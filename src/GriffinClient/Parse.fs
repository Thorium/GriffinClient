/// Griffin response JSON to domain records.
module internal Griffin.Parse

open System.Text.Json
open Griffin.Json

let private optionalString name e = tryString name e |> ValueOption.defaultValue ""

let private flag name e = tryBool name e |> ValueOption.defaultValue false

let errors (e: JsonElement) : GriffinError list =
    array "errors" e
    |> List.map (fun err ->
        let source = tryObject "source" err

        { Title = optionalString "title" err
          Detail =
            match tryString "detail" err, strings "detail" err with
            | ValueSome detail, _ -> ValueSome detail
            | ValueNone, [] -> ValueNone
            | ValueNone, items -> ValueSome(String.concat ", " items)
          Code = tryString "code" err
          SourcePointer =
            source
            |> ValueOption.bind (fun s ->
                match tryString "pointer" s with
                | ValueSome p -> ValueSome p
                | ValueNone -> tryString "header" s)
          About = tryObject "links" err |> ValueOption.bind (tryString "about") })

/// A list response: items under itemsKey, links.prev / links.next. An item that does not parse goes to
/// Unparsed instead of failing the page.
let page (itemsKey: string) (item: JsonElement -> 'T) (e: JsonElement) : Page<'T> =
    let links = tryObject "links" e

    let parsed =
        array itemsKey e
        |> List.map (fun i ->
            try
                Ok(item i)
            with InvalidJson reason ->
                Error { Reason = reason; Raw = i.GetRawText() })

    { Items = parsed |> List.choose (function Ok v -> Some v | Error _ -> None)
      Unparsed = parsed |> List.choose (function Error u -> Some u | Ok _ -> None)
      Next = links |> ValueOption.bind (tryString "next")
      Prev = links |> ValueOption.bind (tryString "prev") }

let index (e: JsonElement) : Index =
    { OrganizationUrl = required "organization-url" tryString e
      Raw = e.GetRawText() }

let organization (url: string) (e: JsonElement) : Organization =
    { Url = url
      DisplayName = optionalString "display-name" e
      Mode = required "organization-mode" (tryParse OrganizationMode.Parse) e
      OwnLegalPersonUrl = tryString "own-legal-person-url" e
      LegalPersonsUrl = required "organization-legal-persons-url" tryString e
      BankAccountsUrl = required "organization-bank-accounts-url" tryString e
      BankProductsUrl = required "organization-bank-account-products-url" tryString e
      WorkflowsUrl = required "organization-workflows-url" tryString e
      PaymentsUrl = tryString "organization-payments-url" e
      EventsUrl = tryString "organization-events-url" e
      WebhooksUrl = tryString "organization-webhooks-url" e
      Raw = e.GetRawText() }

let bankProduct (e: JsonElement) : BankProduct =
    { Url = required "bank-account-product-url" tryString e
      DisplayName = optionalString "display-name" e
      ProductClass = optionalString "bank-account-product-class" e
      FundingType = tryParse FundingType.Parse "funding-type" e
      OwnerType = tryParse LegalPersonType.Parse "owner-type" e
      Status = tryString "bank-account-product-status" e
      AccountsUrl = required "bank-account-product-accounts-url" tryString e
      Raw = e.GetRawText() }

let workflow (e: JsonElement) : Workflow =
    let legalPersonType = optionalString "legal-person-type" e

    { Url = required "workflow-url" tryString e
      DisplayName = optionalString "display-name" e
      LegalPersonType = LegalPersonType.Parse legalPersonType
      Disabled = flag "disabled?" e
      RequiredClaimTypes =
        tryObject "required-claim-types" e
        |> ValueOption.map (strings legalPersonType)
        |> ValueOption.defaultValue []
      Raw = e.GetRawText() }

let decision (e: JsonElement) : Decision =
    { Outcome = required "decision-outcome" (tryParse DecisionOutcome.Parse) e
      VerificationUrl = tryString "verification-url" e
      LegalPersonUrl = tryString "legal-person-url" e
      DecisionMaker = tryParse DecisionMaker.Parse "decision-maker" e
      Notes = optionalString "decision-notes" e
      DeclineReasons =
        array "decline-reasons" e
        |> List.map (fun r ->
            { Code = optionalString "decline-reason-code" r
              Title = optionalString "decline-reason-title" r
              Description = optionalString "decline-reason-description" r
              ClaimTypes = strings "decline-reason-claim-types" r })
      CreatedAt = tryTimestamp "created-at" e }

let legalPerson (e: JsonElement) : LegalPerson =
    { Url = required "legal-person-url" tryString e
      DisplayName = optionalString "display-name" e
      LegalPersonType = required "legal-person-type" (tryParse LegalPersonType.Parse) e
      Status = tryParse LegalPersonStatus.Parse "legal-person-status" e
      ApplicationStatus = tryParse ApplicationStatus.Parse "application-status" e
      LatestDecision = tryObject "latest-decision" e |> ValueOption.map decision
      ClaimsUrl = tryString "legal-person-claims-url" e
      VerificationsUrl = tryString "legal-person-verifications-url" e
      DecisionsUrl = tryString "legal-person-decisions-url" e
      PayeesUrl = tryString "legal-person-bank-payees-url" e
      CreatedAt = tryTimestamp "created-at" e
      Raw = e.GetRawText() }

let verification (e: JsonElement) : Verification =
    { Url = required "verification-url" tryString e
      LegalPersonUrl = required "legal-person-url" tryString e
      WorkflowUrl = tryString "workflow-url" e
      Status = required "verification-status" (tryParse VerificationStatus.Parse) e
      RiskRating = tryParse RiskRating.Parse "risk-rating" e
      ResourcesUrl = tryString "verification-resources-url" e
      CreatedAt = tryTimestamp "created-at" e
      Raw = e.GetRawText() }

let private bankAddress (e: JsonElement) : BankAddress =
    { AccountHolder = optionalString "account-holder" e
      BankId = optionalString "bank-id" e
      BankIdCode = optionalString "bank-id-code" e
      AccountNumber = optionalString "account-number" e
      AccountNumberCode = optionalString "account-number-code" e }

let bankAccount (e: JsonElement) : BankAccount =
    { Url = required "account-url" tryString e
      DisplayName = optionalString "display-name" e
      Status = required "account-status" (tryParse AccountStatus.Parse) e
      ProductType = tryParse BankProductType.Parse "bank-product-type" e
      Balance = tryMoney "account-balance" e
      AvailableBalance = tryMoney "available-balance" e
      BankAddresses = array "bank-addresses" e |> List.map bankAddress
      OwnerUrl = tryString "owner-url" e
      BeneficiaryUrl = tryString "beneficiary-url" e
      PaymentsUrl = tryString "account-payments-url" e
      TransactionsUrl = tryString "account-transactions-url" e
      CloseUrl = tryString "close-account-url" e
      ProductUrl = tryString "bank-account-product-url" e
      AdmissionsUrl = tryString "account-admissions-url" e
      SubmissionsUrl = tryString "account-submissions-url" e
      RestrictionsUrl = tryString "bank-account-restrictions-url" e
      StatementsUrl = tryString "account-statements-url" e
      MandatesUrl = tryString "account-mandates-url" e
      PooledFunds = flag "pooled-funds" e
      Restricted = flag "account-restricted" e
      CreatedAt = tryTimestamp "created-at" e
      Raw = e.GetRawText() }

let payee (e: JsonElement) : Payee =
    { Url = required "payee-url" tryString e
      LegalPersonUrl = optionalString "legal-person-url" e
      Account =
        { AccountHolder = optionalString "account-holder" e
          SortCode = optionalString "bank-id" e
          AccountNumber = optionalString "account-number" e }
      Status = required "payee-status" (tryParse PayeeStatus.Parse) e
      CopRequestUrl = tryString "cop-request-url" e
      Raw = e.GetRawText() }

let copCheck (e: JsonElement) : CopCheck =
    let response = required "cop-response" tryObject e

    { Url = required "cop-request-url" tryString e
      Status = required "status" (tryParse CopStatus.Parse) response
      Result = required "result" (tryParse CopResult.Parse) response
      Reason = tryParse CopReason.Parse "reason" response
      SuggestedName = tryString "suggested-name" response
      SuggestedAccountClassification = tryParse AccountClassification.Parse "suggested-account-classification" response
      Raw = e.GetRawText() }

let idvChecks (e: JsonElement) : IdvCheck list =
    array "verification-resources" e
    |> List.filter (fun r -> tryString "verification-resource-type" r = ValueSome "idv-check")
    |> List.map (fun r ->
        { ResourceId = required "verification-resource-id" tryString r
          DisplayName = optionalString "display-name" r
          LegalPersonUrl = optionalString "legal-person-url" r
          Status = required "idv-check-status" (tryParse IdvCheckStatus.Parse) r
          Url = required "idv-check-url" tryString r
          ExpiresAt = tryTimestamp "idv-check-expires-at" r })

let claimRecord (e: JsonElement) : ClaimRecord =
    { Url = required "claim-url" tryString e
      ClaimType = required "claim-type" tryString e
      CreatedAt = tryTimestamp "created-at" e
      Raw = e.GetRawText() }

let restriction (e: JsonElement) : Restriction =
    { Url = required "bank-account-restriction-url" tryString e
      AccountUrl = required "account-url" tryString e
      RestrictionType = tryParse RestrictionType.Parse "restriction-type" e
      Status = required "restriction-status" (tryParse RestrictionStatus.Parse) e
      Reason = tryParse RestrictionReason.Parse "restriction-reason-code" e
      LegalStatus = tryParse RestrictionLegalStatus.Parse "restriction-legal-status" e
      LiftUrl = tryString "lift-bank-account-restriction-url" e
      UpdateUrl = tryString "update-bank-account-restriction-url" e
      CreatedAt = tryTimestamp "created-at" e
      Raw = e.GetRawText() }

let statement (e: JsonElement) : Statement =
    { Url = required "statement-url" tryString e
      AccountUrl = required "account-url" tryString e
      Format = required "format" (tryParse StatementFormat.Parse) e
      StartDate = required "start-date" tryString e
      EndDate = required "end-date" tryString e
      Status = required "status" (tryParse StatementStatus.Parse) e
      DownloadUrl = tryString "statement-download-url" e
      CreatedAt = tryTimestamp "created-at" e
      Raw = e.GetRawText() }

let private mandateAmount (collection: JsonElement) =
    match tryString "amount-type" collection with
    | ValueSome "fixed" -> tryMoney "amount" collection |> ValueOption.map Fixed
    | ValueSome "variable" -> ValueSome(Variable(tryMoney "max-amount" collection))
    | _ -> ValueNone

let mandate (e: JsonElement) : Mandate =
    { Url = required "mandate-url" tryString e
      AccountUrl = tryObject "debtor" e |> ValueOption.bind (tryString "account-url")
      Status = required "status" (tryParse MandateStatus.Parse) e
      Reference = optionalString "reference" e
      Frequency = tryParse CollectionFrequency.Parse "collection-frequency" e
      Amount = tryObject "collection-amount" e |> ValueOption.bind mandateAmount
      CreditorName = tryObject "creditor" e |> ValueOption.bind (tryString "account-holder")
      CancellationReason = tryParse MandateCancellationReason.Parse "cancellation-reason" e
      PaymentsUrl = tryString "mandate-payments-url" e
      LastUsedAt = tryTimestamp "last-used-at" e
      CreatedAt = tryTimestamp "created-at" e
      Raw = e.GetRawText() }

let payment (e: JsonElement) : Payment =
    { Url = required "payment-url" tryString e
      Direction = tryParse PaymentDirection.Parse "payment-direction" e
      Amount = required "payment-amount" tryMoney e
      Reference = tryString "payment-reference" e
      SubmissionsUrl = tryString "payment-submissions-url" e
      LatestSubmissionUrl = tryString "latest-submission-url" e
      CreatedAt = tryTimestamp "created-at" e
      Raw = e.GetRawText() }

let submission (e: JsonElement) : Submission =
    let scheme = tryObject "submission-scheme-information" e

    { Url = required "submission-url" tryString e
      PaymentUrl = required "payment-url" tryString e
      AccountUrl = tryString "account-url" e
      Status = required "submission-status" (tryParse SubmissionStatus.Parse) e
      StatusReason = tryString "submission-status-reason" e
      Scheme = scheme |> ValueOption.bind (tryParse PaymentScheme.Parse "payment-scheme")
      EndToEndId = scheme |> ValueOption.bind (tryString "end-to-end-identification")
      SchemeStatusCode = scheme |> ValueOption.bind (tryString "scheme-status-code")
      CreatedAt = tryTimestamp "created-at" e
      Raw = e.GetRawText() }

let admission (e: JsonElement) : Admission =
    { Url = required "admission-url" tryString e
      AccountUrl = required "account-url" tryString e
      PaymentUrl = required "payment-url" tryString e
      Status = required "admission-status" (tryParse AdmissionStatus.Parse) e
      Scheme =
        tryObject "admission-scheme-information" e
        |> ValueOption.bind (tryParse PaymentScheme.Parse "payment-scheme")
      CreatedAt = tryTimestamp "created-at" e
      Raw = e.GetRawText() }

let transaction (e: JsonElement) : Transaction =
    { Url = required "account-transaction-url" tryString e
      AccountUrl = required "account-url" tryString e
      BalanceChange = required "balance-change" tryMoney e
      Direction = required "balance-change-direction" (tryParse BalanceDirection.Parse) e
      AccountBalance = required "account-balance" tryMoney e
      Origin = tryParse TransactionOrigin.Parse "transaction-origin-type" e
      Reference = tryString "reference" e
      Description = tryString "description" e
      PaymentUrl = tryString "payment-url" e
      EffectiveAt = tryTimestamp "effective-at" e
      PostedAt = tryTimestamp "post-datetime" e
      Raw = e.GetRawText() }

let webhook (e: JsonElement) : WebhookEndpoint =
    { Url = required "webhook-url" tryString e
      DestinationUrl = required "webhook-destination-url" tryString e
      Description = tryString "webhook-description" e
      State = required "webhook-state" (tryParse WebhookState.Parse) e
      ActivateUrl = tryString "activate-webhook-url" e
      DeactivateUrl = tryString "deactivate-webhook-url" e
      TestUrl = tryString "test-webhook-url" e
      Raw = e.GetRawText() }

let hold (e: JsonElement) : Hold =
    { Url = required "hold-url" tryString e
      AccountUrl = required "account-url" tryString e
      Amount = required "amount" tryMoney e
      Direction = required "direction" (tryParse BalanceDirection.Parse) e
      Status = required "status" (tryParse HoldStatus.Parse) e
      OriginType = optionalString "origin-type" e
      ReleaseReason = tryString "release-reason" e
      ExpiresAt = tryTimestamp "expires-at" e
      TransactionUrls =
        array "account-transactions" e
        |> List.choose (fun t ->
            match tryString "account-transaction-url" t with
            | ValueSome url -> Some url
            | ValueNone -> None)
      CreatedAt = tryTimestamp "created-at" e
      UpdatedAt = tryTimestamp "updated-at" e
      Raw = e.GetRawText() }

let private companyAddress (e: JsonElement) : CompanyAddress =
    { BuildingNumber = tryString "building-number" e
      BuildingName = tryString "building-name" e
      StreetName = tryString "street-name" e
      City = tryString "city" e
      PostalCode = tryString "postal-code" e
      CountryCode = tryString "country-code" e }

let company (e: JsonElement) : Company =
    { Number = required "entity-registration-number" tryString e
      Name = required "entity-name" tryString e
      Status = tryString "company-status" e
      CorporationType = tryString "corporation-type" e
      IncorporatedOn = tryDate "date-of-incorporation" e
      Address = tryObject "company-address" e |> ValueOption.map companyAddress
      SicCodes = strings "sic-codes" e
      AccountsOverdue = flag "accounts-overdue" e
      ConfirmationStatementOverdue = flag "confirmation-statement-overdue" e
      LatestAccountsOn = tryDate "date-of-latest-accounts" e
      LatestConfirmationStatementOn = tryDate "date-of-latest-confirmation-statement" e
      Directors =
        array "directors" e
        |> List.map (fun d ->
            { DisplayName = optionalString "display-name" d
              AppointedOn = tryDate "director-appointed-on" d
              BirthMonth = tryInt "month-of-birth" d
              BirthYear = tryInt "year-of-birth" d
              CompaniesHouseUrl = tryString "companies-house-url" d })
      PersonsWithSignificantControl =
        array "persons-with-significant-control" e
        |> List.map (fun p ->
            { DisplayName = optionalString "display-name" p
              GivenName = tryString "given-name" p
              Surname = tryString "surname" p
              NaturesOfControl = strings "natures-of-control" p
              BirthMonth = tryInt "month-of-birth" p
              BirthYear = tryInt "year-of-birth" p
              CompaniesHouseUrl = tryString "companies-house-url" p })
      Raw = e.GetRawText() }

let poolMember (e: JsonElement) : PoolMember =
    { LegalPersonUrl = required "legal-person-url" tryString e
      CreatedAt = tryTimestamp "created-at" e }

let tellConsent (e: JsonElement) : TellConsent =
    { Url = required "tell-consent-url" tryString e
      ConsentId = required "tell-consent-id" tryString e
      GatewayId = optionalString "tell-gateway-id" e
      Scope = required "tell-consent-scope" (tryParse TellConsentScope.Parse) e
      Status = optionalString "tell-consent-status" e
      Permissions = strings "tell-consent-permissions" e
      BankAccountUrls = strings "bank-account-urls" e
      Expiry = tryTimestamp "tell-consent-expiry" e
      ConsumedAt = tryTimestamp "tell-consent-consumed-at" e
      RevokedAt = tryTimestamp "tell-consent-revoked-at" e
      CreatedAt = tryTimestamp "created-at" e
      Raw = e.GetRawText() }

let webhookKeys (e: JsonElement) : WebhookKey list =
    array "keys" e
    |> List.map (fun k ->
        { KeyId = required "kid" tryString k
          Algorithm = optionalString "alg" k
          Curve = optionalString "crv" k
          X = required "x" tryString k
          Y = required "y" tryString k })
