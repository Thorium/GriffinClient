/// Griffin endpoints. Griffin is navigated by links: every resource carries the URLs of related resources, so
/// most functions take the URL (a field of an earlier result) rather than an id. Every call returns
/// Griffin's errors as ApiError instead of throwing; cancelling the caller's token still throws.
module Griffin.Api

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// The absolute URI of a Griffin link. Relative links ("/...") resolve against BaseUrl; absolute links are
/// followed only on BaseUrl's scheme and host, so the API key is never sent anywhere else.
let resolveUrl (config: GriffinConfig) (link: string) : Result<Uri, GriffinFailure> =
    match Uri.TryCreate(config.BaseUrl, UriKind.Absolute) with
    | false, _ -> Error(InvalidRequest $"BaseUrl '{config.BaseUrl}' is not an absolute URL")
    | true, baseUri ->
        match Uri.TryCreate(link, UriKind.Absolute) with
        | true, absolute when absolute.Scheme = Uri.UriSchemeHttp || absolute.Scheme = Uri.UriSchemeHttps ->
            if absolute.Scheme = baseUri.Scheme && absolute.Authority = baseUri.Authority then
                Ok absolute
            else
                Error(ForeignLink link)
        | _ when link.StartsWith("/", StringComparison.Ordinal) && not (link.StartsWith("//", StringComparison.Ordinal)) ->
            Ok(Uri(baseUri, link))
        | _ -> Error(ForeignLink link)

/// link without the legacy "/v0" prefix. Griffin returns both forms for the same resource (event payloads
/// still carry /v0 links), so compare and store links in this form.
let canonicalLink (link: string) =
    if link.StartsWith("/v0/", StringComparison.Ordinal) then link.Substring 3 else link

/// link with query parameters appended, e.g. withQuery url [ "page[size]", "200"; "sort", "-created-at" ].
let withQuery (link: string) (parameters: seq<string * string>) =
    match List.ofSeq parameters with
    | [] -> link
    | parameters ->
        let query =
            parameters
            |> List.map (fun (k, v) -> Uri.EscapeDataString k + "=" + Uri.EscapeDataString v)
            |> String.concat "&"

        link + (if link.Contains "?" then "&" else "?") + query

/// Griffin's errors array as ApiError; an empty body is ApiError with no errors, so callers still match on status.
let private failureOf status (text: string) =
    let parsed =
        if String.IsNullOrWhiteSpace text then
            ValueSome []
        else
            try
                use doc = JsonDocument.Parse text

                match Parse.errors doc.RootElement with
                | [] -> ValueNone
                | errors -> ValueSome errors
            with :? JsonException ->
                ValueNone

    match parsed with
    | ValueSome errors -> ApiError(status, errors)
    | ValueNone -> InvalidResponse $"HTTP {status}: {text}"

let private signHeaders (client: GriffinClient) (request: HttpRequestMessage) (body: byte[]) ct =
    task {
        match client.Config.Signer with
        | ValueNone -> return Ok()
        | ValueSome _ when
            client.Config.SignatureLifetime <= TimeSpan.Zero
            || client.Config.SignatureLifetime > GriffinConfig.MaxSignatureLifetime
            ->
            return Error(InvalidRequest $"SignatureLifetime {client.Config.SignatureLifetime} is not within (0, 300] seconds")
        | ValueSome signer ->
            let uri = request.RequestUri

            let input: MessageSignature.SigningInput =
                { Method = request.Method.Method
                  Authority = (if uri.IsDefaultPort then uri.Host else $"{uri.Host}:{uri.Port}")
                  Path = uri.AbsolutePath
                  Query = uri.Query
                  Body = body
                  ContentType = string request.Content.Headers.ContentType }

            try
                let! headers =
                    MessageSignature.signAsync
                        signer
                        input
                        DateTimeOffset.UtcNow
                        client.Config.SignatureLifetime
                        (Guid.NewGuid().ToString())
                        ct

                for name, value in headers do
                    request.Headers.TryAddWithoutValidation(name, value) |> ignore

                return Ok()
            with e when not (e :? OperationCanceledException && ct.IsCancellationRequested) ->
                return Error(SigningFailed e)
    }

/// Sends an authenticated (and, when configured, signed) request and hands a success response to read.
let private exchangeAsync
    (client: GriffinClient)
    (method: HttpMethod)
    (link: string)
    (body: obj)
    (read: HttpResponseMessage -> Task<Result<'T, GriffinFailure>>)
    (ct: CancellationToken)
    : Task<Result<'T, GriffinFailure>> =
    task {
        match resolveUrl client.Config link with
        | Error e -> return Error e
        | Ok uri ->
            use request = new HttpRequestMessage(method, uri)
            request.Headers.TryAddWithoutValidation("Authorization", "GriffinAPIKey " + client.Config.ApiKey) |> ignore
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")
            request.Headers.Add("Griffin-Version", client.Config.ApiVersion)

            // Signed requests always carry content headers, because Griffin requires the signature to cover
            // content-type and content-length; a body-less one sends Content-Length: 0.
            let content =
                match isNull body, client.Config.Signer with
                | false, _ -> ValueSome(JsonSerializer.SerializeToUtf8Bytes(body, Json.options))
                | true, ValueSome _ -> ValueSome Array.empty
                | true, ValueNone -> ValueNone

            content
            |> ValueOption.iter (fun bytes ->
                let c = new ByteArrayContent(bytes)
                c.Headers.ContentType <- MediaTypeHeaderValue "application/json"
                c.Headers.ContentLength <- Nullable(int64 bytes.Length)
                request.Content <- c)

            match! signHeaders client request (content |> ValueOption.defaultValue Array.empty) ct with
            | Error e -> return Error e
            | Ok() ->
                try
                    use! response = client.Http.SendAsync(request, ct)

                    if response.IsSuccessStatusCode then
                        return! read response
                    else
                        let! text = response.Content.ReadAsStringAsync()
                        return Error(failureOf (int response.StatusCode) text)
                with
                | :? HttpRequestException as e -> return Error(TransportError e)
                | :? OperationCanceledException as e when not ct.IsCancellationRequested -> return Error(TransportError e)
    }

let private parseJson (parse: JsonElement -> 'T) (response: HttpResponseMessage) =
    task {
        let! text = response.Content.ReadAsStringAsync()

        try
            use doc = JsonDocument.Parse(if String.IsNullOrWhiteSpace text then "null" else text)
            return Ok(parse doc.RootElement)
        with
        | :? JsonException as e -> return Error(InvalidResponse e.Message)
        | Json.InvalidJson message -> return Error(InvalidResponse message)
    }

/// Sends method to link with body and parses a success reply with parse. body is any object System.Text.Json
/// serializes, usually an anonymous record with Griffin's kebab-case names (``{| ``display-name`` = "Ada" |}``);
/// null properties are left out, and a null body sends none. An empty success body (204) reaches parse as JSON
/// null. Adds the API key, Griffin-Version and, when configured, the request signature. Use it for endpoints
/// this module has no function for.
let sendAsync
    (client: GriffinClient)
    (method: HttpMethod)
    (link: string)
    (body: obj)
    (parse: JsonElement -> 'T)
    (ct: CancellationToken)
    : Task<Result<'T, GriffinFailure>> =
    exchangeAsync client method link body (parseJson parse) ct

let getAsync client link parse ct = sendAsync client HttpMethod.Get link null parse ct

let postAsync client link (body: obj) parse ct = sendAsync client HttpMethod.Post link body parse ct

/// Same as sendAsync, returning the success body as JSON text for the caller's own parser.
let sendRawAsync client method link (body: obj) ct =
    sendAsync client method link body _.GetRawText() ct

/// PATCH, which netstandard2.0's HttpMethod lacks.
let private patch = HttpMethod("PATCH")

/// The bytes at a Griffin link, e.g. Statement.DownloadUrl, fetched with the API key (and signature). A link
/// to another host is ForeignLink: fetch such a link (a pre-signed URL, say) yourself, with an HttpClient that
/// carries no credentials.
let downloadAsync (client: GriffinClient) (link: string) (ct: CancellationToken) : Task<Result<byte[], GriffinFailure>> =
    let readBytes (response: HttpResponseMessage) =
        task {
            let! bytes = response.Content.ReadAsByteArrayAsync()
            return Ok bytes
        }

    exchangeAsync client HttpMethod.Get link null readBytes ct

/// Every page of a list, starting at link: fetch is one of the list functions below. The result has every
/// page's Items and Unparsed, and no Next or Prev.
let allPagesAsync
    (fetch: string -> CancellationToken -> Task<Result<Page<'T>, GriffinFailure>>)
    (link: string)
    (ct: CancellationToken)
    : Task<Result<Page<'T>, GriffinFailure>> =
    let rec loop (next: string) (pages: Page<'T> list) =
        task {
            match! fetch next ct with
            | Error e -> return Error e
            | Ok page ->
                match page.Next with
                | ValueSome n -> return! loop n (page :: pages)
                | ValueNone ->
                    let all = List.rev (page :: pages)

                    return
                        Ok
                            { Items = all |> List.collect _.Items
                              Unparsed = all |> List.collect _.Unparsed
                              Next = ValueNone
                              Prev = ValueNone }
        }

    loop link []

/// Runs send only for a valid account; otherwise InvalidRequest, nothing sent.
let private withValidAccount (account: UkAccount) (send: UkAccount -> Task<Result<'T, GriffinFailure>>) =
    match UkAccount.validate account with
    | Ok valid -> send valid
    | Error reason -> Task.FromResult(Error(InvalidRequest reason))

// Navigation

/// 204 when the API is reachable and the key is valid.
let pingAsync (client: GriffinClient) ct = getAsync client "/ping" ignore ct

let getIndexAsync (client: GriffinClient) ct = getAsync client "/index" Parse.index ct

let getOrganizationAsync (client: GriffinClient) (organizationUrl: string) ct =
    getAsync client organizationUrl (Parse.organization organizationUrl) ct

/// The index, then the organization it points to: the usual first call.
let getOwnOrganizationAsync (client: GriffinClient) ct =
    task {
        match! getIndexAsync client ct with
        | Error e -> return Error e
        | Ok index -> return! getOrganizationAsync client index.OrganizationUrl ct
    }

// Products and workflows

/// Organization.BankProductsUrl: the products accounts can be opened against.
let listBankProductsAsync (client: GriffinClient) (link: string) ct =
    getAsync client link (Parse.page "bank-account-products" Parse.bankProduct) ct

/// Organization.WorkflowsUrl: the onboarding workflows a verification can run.
let listWorkflowsAsync (client: GriffinClient) (link: string) ct =
    getAsync client link (Parse.page "workflows" Parse.workflow) ct

// Legal persons and onboarding

/// Creates a legal person in Organization.LegalPersonsUrl; claims come from the Claims module.
let createLegalPersonAsync
    (client: GriffinClient)
    (legalPersonsUrl: string)
    (displayName: string)
    (legalPersonType: LegalPersonType)
    (claims: seq<Claim>)
    ct
    =
    let body =
        {| ``display-name`` = displayName
           ``legal-person-type`` = legalPersonType.Value
           claims = claims |> Seq.map Claim.toJson |> Array.ofSeq |}

    postAsync client legalPersonsUrl body Parse.legalPerson ct

let getLegalPersonAsync (client: GriffinClient) (legalPersonUrl: string) ct =
    getAsync client legalPersonUrl Parse.legalPerson ct

/// Organization.LegalPersonsUrl, or a Page's Next. Filters go in the query, e.g.
/// withQuery url [ "filter[application-status][eq]", "accepted" ].
let listLegalPersonsAsync (client: GriffinClient) (link: string) ct =
    getAsync client link (Parse.page "legal-persons" Parse.legalPerson) ct

/// Renames a legal person.
let updateLegalPersonAsync (client: GriffinClient) (legalPersonUrl: string) (displayName: string) ct =
    sendAsync client HttpMethod.Put legalPersonUrl {| ``display-name`` = displayName |} Parse.legalPerson ct

/// Adds or replaces one claim of a legal person (LegalPerson.ClaimsUrl), e.g. to correct a declined one.
let addClaimAsync (client: GriffinClient) (claimsUrl: string) (claim: Claim) ct =
    postAsync client claimsUrl (Claim.toJson claim) ignore ct

/// LegalPerson.ClaimsUrl: the claims a legal person carries. Filter with
/// withQuery url [ "filter[verification-status][eq]", "unverified" ].
let listClaimsAsync (client: GriffinClient) (link: string) ct =
    getAsync client link (Parse.page "claims" Parse.claimRecord) ct

/// Removes a claim (ClaimRecord.Url).
let deleteClaimAsync (client: GriffinClient) (claimUrl: string) ct =
    sendAsync client HttpMethod.Delete claimUrl null ignore ct

/// Runs workflowUrl's checks against the legal person (LegalPerson.VerificationsUrl).
let createVerificationAsync (client: GriffinClient) (verificationsUrl: string) (workflowUrl: string) ct =
    postAsync client verificationsUrl {| ``workflow-url`` = workflowUrl |} Parse.verification ct

let getVerificationAsync (client: GriffinClient) (verificationUrl: string) ct =
    getAsync client verificationUrl Parse.verification ct

/// LegalPerson.VerificationsUrl, or a Page's Next.
let listVerificationsAsync (client: GriffinClient) (link: string) ct =
    getAsync client link (Parse.page "verifications" Parse.verification) ct

/// The ID document and selfie checks of a verification (Verification.ResourcesUrl). In live mode send the
/// customer to IdvCheck.Url; Griffin publishes the link with the verification-resource-created event.
let getIdvChecksAsync (client: GriffinClient) (resourcesUrl: string) ct =
    getAsync client resourcesUrl Parse.idvChecks ct

/// LegalPerson.DecisionsUrl, or a Page's Next: every decision, newest first.
let listDecisionsAsync (client: GriffinClient) (link: string) ct =
    getAsync client link (Parse.page "decisions" Parse.decision) ct

/// Decides verification yourself (LegalPerson.DecisionsUrl): one whose checks are complete and that Griffin
/// referred for manual review rather than deciding. Needs the organization's can-decide-on-verifications?
/// permission; an already decided verification answers 422.
let createDecisionAsync
    (client: GriffinClient)
    (decisionsUrl: string)
    (verification: Verification)
    (outcome: DecisionOutcome)
    (notes: string)
    ct
    =
    let body =
        {| ``verification-url`` = verification.Url
           ``decision-outcome`` = outcome.Value
           ``decision-notes`` = notes |}

    postAsync client decisionsUrl body Parse.decision ct

// Bank accounts

/// Opens an account against a product (BankProduct.AccountsUrl). It starts Opening; poll getBankAccountAsync
/// or wait for the account-status-updated event until it is Open. Some products need no beneficiary.
let openBankAccountAsync
    (client: GriffinClient)
    (productAccountsUrl: string)
    (displayName: string)
    (ownerUrl: string)
    (beneficiaryUrl: string voption)
    ct
    =
    let body =
        {| ``display-name`` = displayName
           ``owner-url`` = ownerUrl
           ``beneficiary-url`` = ValueOption.toObj beneficiaryUrl |}

    postAsync client productAccountsUrl body Parse.bankAccount ct

let getBankAccountAsync (client: GriffinClient) (accountUrl: string) ct =
    getAsync client accountUrl Parse.bankAccount ct

/// Organization.BankAccountsUrl, or a Page's Next.
let listBankAccountsAsync (client: GriffinClient) (link: string) ct =
    getAsync client link (Parse.page "accounts" Parse.bankAccount) ct

/// Starts closing the account (BankAccount.CloseUrl).
let closeBankAccountAsync (client: GriffinClient) (closeUrl: string) ct =
    postAsync client closeUrl null Parse.bankAccount ct

/// Renames an account.
let updateBankAccountAsync (client: GriffinClient) (accountUrl: string) (displayName: string) ct =
    sendAsync client patch accountUrl {| ``display-name`` = displayName |} Parse.bankAccount ct

// Search

let private searchAsync client (searchUrl: string) (query: string) item ct =
    postAsync client searchUrl {| ``search-query`` = query |} (Parse.page "search-results" item) ct

/// Legal persons matching query, e.g. a name (Organization.LegalPersonsSearchUrl).
let searchLegalPersonsAsync (client: GriffinClient) (searchUrl: string) (query: string) ct =
    searchAsync client searchUrl query Parse.legalPerson ct

/// Bank accounts matching query, an account number (display names do not match) (Organization.BankAccountsSearchUrl).
let searchBankAccountsAsync (client: GriffinClient) (searchUrl: string) (query: string) ct =
    searchAsync client searchUrl query Parse.bankAccount ct

/// Payments matching query, e.g. a reference (Organization.PaymentsSearchUrl).
let searchPaymentsAsync (client: GriffinClient) (searchUrl: string) (query: string) ct =
    searchAsync client searchUrl query Parse.payment ct

// Holds and pooled accounts

/// BankAccount.HoldsUrl, or a Page's Next: funds reserved for card transactions that have not cleared.
let listHoldsAsync (client: GriffinClient) (link: string) ct =
    getAsync client link (Parse.page "holds" Parse.hold) ct

let getHoldAsync (client: GriffinClient) (holdUrl: string) ct = getAsync client holdUrl Parse.hold ct

/// BankAccount.MembershipUrl, or a Page's Next: whose funds a pooled account holds.
let listPoolMembersAsync (client: GriffinClient) (link: string) ct =
    getAsync client link (Parse.page "pool-members" Parse.poolMember) ct

/// Adds and removes members of a pooled account (BankAccount.MembershipUpdatesUrl), by legal person URL.
let updatePoolMembersAsync (client: GriffinClient) (membershipUpdatesUrl: string) (additions: seq<string>) (deletions: seq<string>) ct =
    let body =
        {| additions = Array.ofSeq additions
           deletions = Array.ofSeq deletions |}

    postAsync client membershipUpdatesUrl body ignore ct

// Companies House

/// A UK company by its Companies House number, with directors and persons with significant control.
let lookupCompanyAsync (client: GriffinClient) (companyNumber: string) ct =
    getAsync client ("/companies-house/companies/" + Uri.EscapeDataString companyNumber) Parse.company ct

// Open banking (tell.money)

/// Registers a consent your customer gave a third-party provider (Organization.TellConsentsUrl), so Griffin
/// answers that provider's requests for the listed accounts. The ids must be a real tell.money consent and
/// gateway; made-up ones pass validation, then Griffin answers 400 "Unexpected error".
let registerTellConsentAsync (client: GriffinClient) (tellConsentsUrl: string) (consent: TellConsentRequest) ct =
    let body =
        {| ``tell-consent-id`` = consent.ConsentId
           ``tell-gateway-id`` = consent.GatewayId
           ``tell-consent-scope`` = consent.Scope.Value
           ``tell-consent-permissions`` = consent.Permissions
           ``bank-account-urls`` = consent.BankAccountUrls
           ``tell-consent-expiry`` =
            consent.Expiry
            |> ValueOption.map (fun e -> e.ToString("o", Globalization.CultureInfo.InvariantCulture))
            |> ValueOption.toObj |}

    postAsync client tellConsentsUrl body Parse.tellConsent ct

let getTellConsentAsync (client: GriffinClient) (consentUrl: string) ct =
    getAsync client consentUrl Parse.tellConsent ct

/// Revokes a consent (TellConsent.RevokeUrl); the provider loses access.
let revokeTellConsentAsync (client: GriffinClient) (revokeUrl: string) ct =
    postAsync client revokeUrl null Parse.tellConsent ct

// Restrictions

/// Freezes or blocks an account (BankAccount.RestrictionsUrl). Restrictions are independent: each is lifted on
/// its own.
let applyRestrictionAsync
    (client: GriffinClient)
    (restrictionsUrl: string)
    (restrictionType: RestrictionType)
    (reason: RestrictionReason)
    (legalStatus: RestrictionLegalStatus)
    (notes: string)
    ct
    =
    let body =
        {| ``restriction-type`` = restrictionType.Value
           ``restriction-reason-code`` = reason.Value
           ``restriction-legal-status`` = legalStatus.Value
           notes = notes |}

    postAsync client restrictionsUrl body Parse.restriction ct

/// BankAccount.RestrictionsUrl, or a Page's Next.
let listRestrictionsAsync (client: GriffinClient) (link: string) ct =
    getAsync client link (Parse.page "bank-account-restrictions" Parse.restriction) ct

let getRestrictionAsync (client: GriffinClient) (restrictionUrl: string) ct =
    getAsync client restrictionUrl Parse.restriction ct

/// Lifts an active restriction (Restriction.LiftUrl).
let liftRestrictionAsync (client: GriffinClient) (liftUrl: string) (notes: string) ct =
    postAsync client liftUrl {| notes = notes |} Parse.restriction ct

// Statements

/// Asks for a statement of the account's transactions from startDate to endDate (BankAccount.StatementsUrl).
/// The period must end before today; sandbox accounts only offer XeroCsv. It starts Pending; poll
/// getStatementAsync until Generated, then downloadAsync its DownloadUrl.
let requestStatementAsync
    (client: GriffinClient)
    (statementsUrl: string)
    (format: StatementFormat)
    (startDate: DateTime)
    (endDate: DateTime)
    ct
    =
    let date (d: DateTime) = d.ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)

    let body =
        {| format = format.Value
           ``start-date`` = date startDate
           ``end-date`` = date endDate |}

    postAsync client statementsUrl body Parse.statement ct

/// BankAccount.StatementsUrl, or a Page's Next.
let listStatementsAsync (client: GriffinClient) (link: string) ct =
    getAsync client link (Parse.page "statements" Parse.statement) ct

let getStatementAsync (client: GriffinClient) (statementUrl: string) ct =
    getAsync client statementUrl Parse.statement ct

// Direct debit mandates

/// BankAccount.MandatesUrl, or a Page's Next: the Direct Debits the account pays.
let listMandatesAsync (client: GriffinClient) (link: string) ct =
    getAsync client link (Parse.page "mandates" Parse.mandate) ct

let getMandateAsync (client: GriffinClient) (mandateUrl: string) ct =
    getAsync client mandateUrl Parse.mandate ct

/// Cancels a Direct Debit mandate, so the originator can no longer collect, e.g. CancelledByDebtor when the
/// account holder asked.
let cancelMandateAsync (client: GriffinClient) (mandateUrl: string) (reason: MandateCancellationReason) ct =
    let body =
        {| status = MandateStatus.Cancelled.Value
           ``cancellation-reason`` = reason.Value |}

    sendAsync client patch mandateUrl body ignore ct

// Payees and Confirmation of Payee

let private copBody
    (requesterLegalPersonUrl: string)
    (account: UkAccount)
    (classification: AccountClassification)
    (secondaryReference: string voption)
    =
    {| ``requester-legal-person-url`` = requesterLegalPersonUrl
       name = account.AccountHolder
       ``bank-id`` = account.SortCode
       ``account-number`` = account.AccountNumber
       ``account-classification`` = classification.Value
       ``secondary-reference-data`` = ValueOption.toObj secondaryReference |}

/// Checks that account.AccountHolder holds the account before paying it (Confirmation of Payee). Run it before
/// creating a payee and pass CopCheck.Url to createPayeeAsync. requesterLegalPersonUrl is the customer asking.
let confirmPayeeAsync
    (client: GriffinClient)
    (organization: Organization)
    (requesterLegalPersonUrl: string)
    (account: UkAccount)
    (classification: AccountClassification)
    (secondaryReference: string voption)
    ct
    =
    withValidAccount account (fun valid ->
        let body = copBody requesterLegalPersonUrl valid classification secondaryReference
        postAsync client (canonicalLink organization.Url + "/cop-request") body Parse.copCheck ct)

/// Sandbox only (Griffin marks it alpha): a Confirmation of Payee answer of the given kind.
let simulateCopAsync
    (client: GriffinClient)
    (organization: Organization)
    (simulation: CopSimulation)
    (requesterLegalPersonUrl: string)
    (account: UkAccount)
    (classification: AccountClassification)
    ct
    =
    withValidAccount account (fun valid ->
        let body = copBody requesterLegalPersonUrl valid classification ValueNone
        let link = $"/simulation{canonicalLink organization.Url}/cop-request/simulate/{simulation.Value}"
        postAsync client link body Parse.copCheck ct)

/// A payee of a legal person (LegalPerson.PayeesUrl); copRequestUrl records the Confirmation of Payee check
/// made for it.
let createPayeeAsync
    (client: GriffinClient)
    (payeesUrl: string)
    (account: UkAccount)
    (copRequestUrl: string voption)
    ct
    =
    withValidAccount account (fun valid ->
        let body =
            {| ``account-holder`` = valid.AccountHolder
               ``bank-id`` = valid.SortCode
               ``account-number`` = valid.AccountNumber
               ``cop-request-url`` = ValueOption.toObj copRequestUrl |}

        postAsync client payeesUrl body Parse.payee ct)

let getPayeeAsync (client: GriffinClient) (payeeUrl: string) ct = getAsync client payeeUrl Parse.payee ct

/// LegalPerson.PayeesUrl, or a Page's Next.
let listPayeesAsync (client: GriffinClient) (link: string) ct =
    getAsync client link (Parse.page "payees" Parse.payee) ct

/// Deactivates a payee, so no further payments go to it.
let deactivatePayeeAsync (client: GriffinClient) (payeeUrl: string) ct =
    sendAsync client patch payeeUrl {| ``payee-status`` = PayeeStatus.Deactivated.Value |} Parse.payee ct

// Payments

let private creditorBody: Creditor -> obj =
    function
    | ToPayee url -> {| ``creditor-type`` = "payee"; ``payee-url`` = url |}
    | ToGriffinAccount url -> {| ``creditor-type`` = "griffin-bank-account"; ``account-url`` = url |}
    | ToUkAccount account ->
        {| ``creditor-type`` = "uk-domestic"
           ``account-holder`` = account.AccountHolder
           ``bank-id`` = account.SortCode
           ``bank-id-code`` = "gbdsc"
           ``account-number`` = account.AccountNumber
           ``account-number-code`` = "bban" |}

/// Creates a payment from the account (BankAccount.PaymentsUrl). Nothing moves until it is submitted.
/// Creation is not idempotent: retrying after a timeout can create a second payment.
/// reference is at most 35 characters; Griffin fills a default when it is ValueNone.
let createPaymentAsync
    (client: GriffinClient)
    (accountPaymentsUrl: string)
    (creditor: Creditor)
    (amount: Money)
    (reference: string voption)
    ct
    =
    let send () =
        let body =
            {| creditor = creditorBody creditor
               ``payment-amount`` = Money.toJson amount
               ``payment-reference`` = ValueOption.toObj reference |}

        postAsync client accountPaymentsUrl body Parse.payment ct

    match creditor with
    | ToUkAccount account -> withValidAccount account (fun _ -> send ())
    | ToPayee _
    | ToGriffinAccount _ -> send ()

/// Submits the payment (Payment.SubmissionsUrl) by Fps or BookTransfer. Submission is idempotent: after a
/// timeout or 5xx, retry; a payment already submitted answers 410 (ApiError(410, _)), then read
/// listSubmissionsAsync.
let submitPaymentAsync (client: GriffinClient) (submissionsUrl: string) (scheme: PaymentScheme) ct =
    postAsync client submissionsUrl {| ``payment-scheme`` = scheme.Value |} Parse.submission ct

let getPaymentAsync (client: GriffinClient) (paymentUrl: string) ct =
    getAsync client paymentUrl Parse.payment ct

/// BankAccount.PaymentsUrl or Organization.PaymentsUrl, or a Page's Next.
let listPaymentsAsync (client: GriffinClient) (link: string) ct =
    getAsync client link (Parse.page "payments" Parse.payment) ct

let getSubmissionAsync (client: GriffinClient) (submissionUrl: string) ct =
    getAsync client submissionUrl Parse.submission ct

/// BankAccount.AdmissionsUrl (inbound payments to the account) or a payment's admissions, or a Page's Next.
let listAdmissionsAsync (client: GriffinClient) (link: string) ct =
    getAsync client link (Parse.page "admissions" Parse.admission) ct

let getAdmissionAsync (client: GriffinClient) (admissionUrl: string) ct =
    getAsync client admissionUrl Parse.admission ct

/// Payment.SubmissionsUrl or BankAccount.SubmissionsUrl: present once a payment has been submitted.
let listSubmissionsAsync (client: GriffinClient) (submissionsUrl: string) ct =
    getAsync client submissionsUrl (Parse.page "submissions" Parse.submission) ct

// Transactions

/// BankAccount.TransactionsUrl, or a Page's Next. Newest first unless the query says sort=post-datetime.
let listTransactionsAsync (client: GriffinClient) (link: string) ct =
    getAsync client link (Parse.page "account-transactions" Parse.transaction) ct

let getTransactionAsync (client: GriffinClient) (transactionUrl: string) ct =
    getAsync client transactionUrl Parse.transaction ct

// Events and webhooks

/// Organization.EventsUrl, or a Page's Next: the same events webhooks deliver.
let listEventsAsync (client: GriffinClient) (link: string) ct =
    getAsync client link (Parse.page "events" Event.ofJson) ct

let getEventAsync (client: GriffinClient) (eventUrl: string) ct = getAsync client eventUrl Event.ofJson ct

/// Registers destinationUrl for the organization's events (Organization.WebhooksUrl). Check State: Griffin may
/// start it active, delivering at once (it does in the sandbox), or inactive until activateWebhookAsync.
let createWebhookAsync (client: GriffinClient) (webhooksUrl: string) (destinationUrl: string) (description: string voption) ct =
    let body =
        {| ``webhook-destination-url`` = destinationUrl
           ``webhook-description`` = ValueOption.toObj description |}

    postAsync client webhooksUrl body Parse.webhook ct

let listWebhooksAsync (client: GriffinClient) (webhooksUrl: string) ct =
    getAsync client webhooksUrl (Parse.page "organization-webhooks" Parse.webhook) ct

let getWebhookAsync (client: GriffinClient) (webhookUrl: string) ct =
    getAsync client webhookUrl Parse.webhook ct

/// Changes a webhook's description; its destination is fixed (register a new webhook to move it).
let updateWebhookAsync (client: GriffinClient) (webhookUrl: string) (description: string) ct =
    sendAsync client patch webhookUrl {| ``webhook-description`` = description |} Parse.webhook ct

/// Removes a webhook (WebhookEndpoint.Url); Griffin stops delivering to it.
let deleteWebhookAsync (client: GriffinClient) (webhookUrl: string) ct =
    sendAsync client HttpMethod.Delete webhookUrl null ignore ct

/// WebhookEndpoint.ActivateUrl.
let activateWebhookAsync (client: GriffinClient) (activateUrl: string) ct =
    postAsync client activateUrl null Parse.webhook ct

/// WebhookEndpoint.DeactivateUrl.
let deactivateWebhookAsync (client: GriffinClient) (deactivateUrl: string) ct =
    postAsync client deactivateUrl null Parse.webhook ct

/// Asks Griffin to deliver a test event to the webhook (WebhookEndpoint.TestUrl).
let testWebhookAsync (client: GriffinClient) (testUrl: string) ct = postAsync client testUrl null ignore ct

/// The keys Griffin signs webhook deliveries with.
let getWebhookKeysAsync (client: GriffinClient) ct =
    getAsync client "/security/public-keys" Parse.webhookKeys ct
