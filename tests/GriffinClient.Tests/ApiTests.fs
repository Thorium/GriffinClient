module ApiTests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Griffin
open FakeGriffin

let private ct = CancellationToken.None

let private expectOk =
    function
    | Ok v -> v
    | Error e -> failwith $"%A{e}"

let private claimOf (claimType: string) (body: JsonElement) =
    body.GetProperty("claims").EnumerateArray()
    |> Seq.find (fun c -> c.GetProperty("claim-type").GetString() = claimType)

[<Fact>]
let ``requests carry the API key, version and JSON accept header and no signature without a signer`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> ok """{"organization-url":"/organizations/og.1"}""")
        let! result = Api.getIndexAsync (clientWith handler) ct
        Assert.Equal("/organizations/og.1", (expectOk result).OrganizationUrl)

        let call = Assert.Single handler.Calls
        Assert.Equal("GET", call.Method)
        Assert.Equal("/index", call.Target)
        Assert.Equal("GriffinAPIKey g-test-key-1", call.Headers["Authorization"])
        Assert.Equal(GriffinConfig.ApiVersion, call.Headers["Griffin-Version"])
        Assert.Equal("application/json", call.Headers["Accept"])
        Assert.False(call.Headers.ContainsKey "Signature")
        Assert.False(call.Headers.ContainsKey "Signature-Input")
    }

[<Fact>]
let ``own organization follows the index and keeps the URL it was read from`` () : Task =
    task {
        use handler =
            new FakeGriffinHandler(fun _ target _ ->
                match target with
                | "/index" -> ok """{"organization-url":"/organizations/og.1"}"""
                | "/organizations/og.1" -> ok organizationJson
                | other -> failwith other)

        let! org = Api.getOwnOrganizationAsync (clientWith handler) ct
        let org = expectOk org
        Assert.Equal("/organizations/og.1", org.Url)
        Assert.Equal(OrganizationMode.TestMode, org.Mode)
        Assert.Equal("/organizations/og.1/bank/products", org.BankProductsUrl)
        Assert.Equal(ValueSome "/organizations/og.1/webhooks", org.WebhooksUrl)
    }

[<Fact>]
let ``legal person request carries display name, type and kebab-case claims`` () : Task =
    task {
        use handler =
            new FakeGriffinHandler(fun _ _ _ ->
                created """{"legal-person-url":"/legal-persons/lp.9","display-name":"Ada Example","legal-person-type":"individual","legal-person-verifications-url":"/legal-persons/lp.9/verifications"}""")

        let claims =
            [ Claims.individualIdentity "Ada" ValueNone "Example" (DateTime(1985, 6, 15))
              Claims.usCitizen false
              Claims.individualIncome (Money.gbp 45000m)
              Claims.employment (Employed("information-technology", ValueSome "software-engineer"))
              Claims.ukResidence (ValueSome "10") ValueNone "Downing Street" "London" "SW1A 2AA" "GB"
              Claims.internationalPaymentsCountries [] ]

        let! person =
            Api.createLegalPersonAsync (clientWith handler) "/organizations/og.1/legal-persons" "Ada Example" LegalPersonType.Individual claims ct

        Assert.Equal(ValueSome "/legal-persons/lp.9/verifications", (expectOk person).VerificationsUrl)

        let call = Assert.Single handler.Calls
        Assert.Equal("POST", call.Method)
        Assert.Equal("/organizations/og.1/legal-persons", call.Target)
        Assert.Equal("application/json", call.ContentHeaders["Content-Type"])
        let body = call.Body.Value
        Assert.Equal("Ada Example", body.GetProperty("display-name").GetString())
        Assert.Equal("individual", body.GetProperty("legal-person-type").GetString())

        let identity = claimOf "individual-identity" body
        Assert.Equal("1985-06-15", identity.GetProperty("date-of-birth").GetString())
        Assert.Equal("Ada", identity.GetProperty("given-name").GetString())
        Assert.False(identity.TryGetProperty "middle-name" |> fst)

        Assert.False((claimOf "us-citizen" body).GetProperty("us-citizen?").GetBoolean())
        Assert.Equal("45000.00", (claimOf "individual-income" body).GetProperty("income").GetProperty("value").GetString())

        let employment = claimOf "employment" body
        Assert.Equal("employed", employment.GetProperty("employment-status").GetString())
        Assert.Equal("software-engineer", employment.GetProperty("occupation").GetString())

        let residence = claimOf "individual-residence" body
        Assert.Equal("10", residence.GetProperty("building-number").GetString())
        Assert.False(residence.TryGetProperty "building-name" |> fst)

        Assert.Equal(0, (claimOf "international-payments-countries" body).GetProperty("international-payments-countries").GetArrayLength())
    }

[<Fact>]
let ``Griffin errors become ApiError with title, detail, code, pointer and docs link`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> griffinError 400 "Invalid enum value" "claims/7/occupation")

        match! Api.getLegalPersonAsync (clientWith handler) "/legal-persons/lp.1" ct with
        | Error(ApiError(400, [ e ])) ->
            Assert.Equal("Invalid enum value", e.Title)
            Assert.Equal(ValueSome "Detail of Invalid enum value", e.Detail)
            Assert.Equal(ValueSome "some-code", e.Code)
            Assert.Equal(ValueSome "claims/7/occupation", e.SourcePointer)
            Assert.Equal(ValueSome "https://docs.griffin.com/docs/errors/x", e.About)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``signature errors keep a detail array and a header source`` () : Task =
    task {
        // Griffin's answer to a signed GET that does not cover content-type and content-length.
        let json =
            """{"errors":[{"title":"Unauthorized","status":"401","detail":["content-type","content-length"],"code":"missing-required-components","source":{"header":"signature-input/griffin-client"}}]}"""

        use handler = new FakeGriffinHandler(fun _ _ _ -> 401, json)

        match! Api.getIndexAsync (clientWith handler) ct with
        | Error(ApiError(401, [ e ])) ->
            Assert.Equal(ValueSome "content-type, content-length", e.Detail)
            Assert.Equal(ValueSome "missing-required-components", e.Code)
            Assert.Equal(ValueSome "signature-input/griffin-client", e.SourcePointer)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``a non-JSON failure becomes InvalidResponse`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> 502, "<html>bad gateway</html>")

        match! Api.getIndexAsync (clientWith handler) ct with
        | Error(InvalidResponse message) -> Assert.Contains("502", message)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``a success body missing a required field becomes InvalidResponse`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> ok """{"display-name":"x"}""")

        match! Api.getBankAccountAsync (clientWith handler) "/bank/accounts/ba.1" ct with
        | Error(InvalidResponse message) -> Assert.Contains("account-url", message)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``links to other hosts are refused before any request, so the key stays with Griffin`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> ok "{}")
        let client = clientWith handler

        for link in [ "https://evil.example/bank/accounts/ba.1"; "//evil.example/x"; "http://api.griffin.com/index"; "index" ] do
            match! Api.getBankAccountAsync client link ct with
            | Error(ForeignLink l) -> Assert.Equal(link, l)
            | other -> failwith $"%s{link}: %A{other}"

        Assert.Empty handler.Calls

        let! _ = Api.getIndexAsync client ct
        let! _ = Api.getBankAccountAsync client "https://api.griffin.com/bank/accounts/ba.1" ct
        Assert.Equal<string list>([ "/index"; "/bank/accounts/ba.1" ], handler.Calls |> List.map _.Target)
    }

[<Fact>]
let ``bank account parses balances, sort code and links`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> ok (accountJson "/bank/accounts/ba.1" "open"))

        let! account = Api.getBankAccountAsync (clientWith handler) "/bank/accounts/ba.1" ct
        let account = expectOk account
        Assert.Equal(AccountStatus.Open, account.Status)
        Assert.Equal(ValueSome BankProductType.EmbeddedAccount, account.ProductType)
        Assert.Equal(ValueSome(Money.gbp 100m), account.Balance)
        Assert.Equal(ValueSome(Money.gbp 90.5m), account.AvailableBalance)
        let address = Assert.Single account.BankAddresses
        Assert.Equal("000001", address.BankId)
        Assert.Equal("00330292", address.AccountNumber)
        Assert.Equal(ValueSome "/bank/accounts/ba.1/payments", account.PaymentsUrl)
    }

[<Fact>]
let ``opening an account sends owner and beneficiary, and leaves out a missing beneficiary`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> created (accountJson "/bank/accounts/ba.2" "opening"))
        let client = clientWith handler
        let! _ = Api.openBankAccountAsync client "/bank/products/bp.1/accounts" "Ada's" "/legal-persons/lp.1" (ValueSome "/legal-persons/lp.1") ct
        let! _ = Api.openBankAccountAsync client "/bank/products/bp.1/accounts" "Ops" "/legal-persons/lp.own" ValueNone ct

        match handler.Calls |> List.map (fun c -> c.Body.Value) with
        | [ first; second ] ->
            Assert.Equal("/legal-persons/lp.1", first.GetProperty("beneficiary-url").GetString())
            Assert.Equal("/legal-persons/lp.own", second.GetProperty("owner-url").GetString())
            Assert.False(second.TryGetProperty "beneficiary-url" |> fst)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``payment bodies for each creditor, with a two-decimal amount string and optional reference`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> created paymentJson)
        let client = clientWith handler
        let pay creditor reference = Api.createPaymentAsync client "/bank/accounts/ba.1/payments" creditor (Money.gbp 25.5m) reference ct
        let! payment =
            pay (ToUkAccount { AccountHolder = "Ada Example"; SortCode = "600130"; AccountNumber = "12345678" }) (ValueSome "Rent")

        let! _ = pay (ToPayee "/payees/py.1") ValueNone
        let! _ = pay (ToGriffinAccount "/bank/accounts/ba.2") ValueNone

        let payment = expectOk payment
        Assert.Equal(Money.gbp 25.5m, payment.Amount)
        Assert.Equal(ValueSome "/payments/pm.1/submissions", payment.SubmissionsUrl)

        match handler.Calls |> List.map (fun c -> c.Body.Value) with
        | [ uk; payee; book ] ->
            let creditor = uk.GetProperty "creditor"
            Assert.Equal("uk-domestic", creditor.GetProperty("creditor-type").GetString())
            Assert.Equal("600130", creditor.GetProperty("bank-id").GetString())
            Assert.Equal("gbdsc", creditor.GetProperty("bank-id-code").GetString())
            Assert.Equal("12345678", creditor.GetProperty("account-number").GetString())
            Assert.Equal("bban", creditor.GetProperty("account-number-code").GetString())
            Assert.Equal("25.50", uk.GetProperty("payment-amount").GetProperty("value").GetString())
            Assert.Equal("GBP", uk.GetProperty("payment-amount").GetProperty("currency").GetString())
            Assert.Equal("Rent", uk.GetProperty("payment-reference").GetString())

            Assert.Equal("payee", payee.GetProperty("creditor").GetProperty("creditor-type").GetString())
            Assert.Equal("/payees/py.1", payee.GetProperty("creditor").GetProperty("payee-url").GetString())
            Assert.False(payee.TryGetProperty "payment-reference" |> fst)

            Assert.Equal("griffin-bank-account", book.GetProperty("creditor").GetProperty("creditor-type").GetString())
            Assert.Equal("/bank/accounts/ba.2", book.GetProperty("creditor").GetProperty("account-url").GetString())
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``submission sends the scheme and parses status and scheme information`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> ok (submissionJson "processing"))
        let! submission = Api.submitPaymentAsync (clientWith handler) "/payments/pm.1/submissions" PaymentScheme.Fps ct
        let submission = expectOk submission
        Assert.Equal(SubmissionStatus.Processing, submission.Status)
        Assert.Equal(ValueSome PaymentScheme.Fps, submission.Scheme)
        Assert.Equal(ValueSome "e2e-1", submission.EndToEndId)
        Assert.Equal(ValueSome "0000", submission.SchemeStatusCode)
        Assert.Equal("fps", (Assert.Single handler.Calls).Body.Value.GetProperty("payment-scheme").GetString())
    }

[<Fact>]
let ``a payment already submitted answers 410 as ApiError`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> griffinError 410 "Gone" "")

        match! Api.submitPaymentAsync (clientWith handler) "/payments/pm.1/submissions" PaymentScheme.BookTransfer ct with
        | Error(ApiError(410, _)) -> ()
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``allPagesAsync follows next links and keeps the order`` () : Task =
    task {
        use handler =
            new FakeGriffinHandler(fun _ target _ ->
                match target with
                | "/bank/accounts/ba.1/transactions" ->
                    ok (pageJson "account-transactions" [ transactionJson "tr.1" "1.00" "credit"; transactionJson "tr.2" "2.00" "debit" ] (ValueSome "/bank/accounts/ba.1/transactions?page[after]=abc"))
                | t when t.StartsWith "/bank/accounts/ba.1/transactions?page" -> ok (pageJson "account-transactions" [ transactionJson "tr.3" "3.00" "credit" ] ValueNone)
                | other -> failwith other)

        let client = clientWith handler
        let! all = Api.allPagesAsync (Api.listTransactionsAsync client) "/bank/accounts/ba.1/transactions" ct
        let all = (expectOk all).Items
        Assert.Equal<string list>([ "/bank/transactions/tr.1"; "/bank/transactions/tr.2"; "/bank/transactions/tr.3" ], all |> List.map _.Url)
        Assert.Equal(BalanceDirection.Debit, all[1].Direction)
        Assert.Equal(ValueSome TransactionOrigin.Payment, all[1].Origin)
        Assert.Equal(Money.gbp 3m, all[2].BalanceChange)
        Assert.Equal(2, handler.Calls.Length)
    }

[<Fact>]
let ``withQuery escapes parameters and canonicalLink drops the v0 prefix`` () =
    Assert.Equal(
        "/organizations/og.1/legal-persons?filter%5Bapplication-status%5D%5Beq%5D=accepted&page%5Bsize%5D=200",
        Api.withQuery "/organizations/og.1/legal-persons" [ "filter[application-status][eq]", "accepted"; "page[size]", "200" ]
    )

    Assert.Equal("/x?a=1&b=2", Api.withQuery "/x?a=1" [ ("b", "2") ])
    Assert.Equal("/bank/accounts/ba.1", Api.canonicalLink "/v0/bank/accounts/ba.1")
    Assert.Equal("/bank/accounts/ba.1", Api.canonicalLink "/bank/accounts/ba.1")

[<Fact>]
let ``ping answers 204 with no body`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> 204, "")
        let! result = Api.pingAsync (clientWith handler) ct
        Assert.Equal(Ok(), result)
    }

[<Fact>]
let ``money keeps at least two decimals and any further precision`` () =
    let value (m: Money) =
        JsonDocument.Parse(JsonSerializer.Serialize(Money.toJson m)).RootElement.GetProperty("value").GetString()

    Assert.Equal("20.00", value (Money.gbp 20m))
    Assert.Equal("0.50", value (Money.gbp 0.5m))
    Assert.Equal("0.005", value (Money.gbp 0.005m))
    Assert.Equal("1000000.00", value (Money.gbp 1000000m))

[<Fact>]
let ``one unreadable item lands in Unparsed instead of failing the page`` () : Task =
    task {
        let broken = """{"account-transaction-url":"/bank/transactions/tr.bad","account-url":"/bank/accounts/ba.1"}"""

        use handler =
            new FakeGriffinHandler(fun _ _ _ ->
                ok (pageJson "account-transactions" [ transactionJson "tr.1" "1.00" "credit"; broken ] ValueNone))

        let! page = Api.listTransactionsAsync (clientWith handler) "/bank/accounts/ba.1/transactions" ct
        let page = expectOk page
        Assert.Equal("/bank/transactions/tr.1", (Assert.Single page.Items).Url)
        let unparsed = Assert.Single page.Unparsed
        Assert.Contains("balance-change", unparsed.Reason)
        Assert.Contains("tr.bad", unparsed.Raw)
    }

[<Fact>]
let ``an unknown status value parses as Other and round-trips`` () =
    Assert.Equal(AccountStatus.Other "frozen-ish", AccountStatus.Parse "frozen-ish")
    Assert.Equal("frozen-ish", (AccountStatus.Parse "frozen-ish").Value)
    Assert.Equal(AccountStatus.Open, AccountStatus.Parse "open")
    Assert.Equal("fraud-1st-party", RestrictionReason.FraudFirstParty.Value)
    Assert.Equal("other", RestrictionReason.OtherReason.Value)
    Assert.Equal("close-match-name", (CopSimulation.Reason CopReason.CloseMatchName).Value)

[<Fact>]
let ``an invalid UK account is refused before any request`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> created paymentJson)
        let client = clientWith handler

        for account, expected in
            [ { AccountHolder = "Ada"; SortCode = "60-01-30"; AccountNumber = "12345678" }, "Sort code"
              { AccountHolder = "Ada"; SortCode = "600130"; AccountNumber = "1234567" }, "Account number"
              { AccountHolder = "Ada"; SortCode = "６00130"; AccountNumber = "12345678" }, "Sort code"
              { AccountHolder = " "; SortCode = "600130"; AccountNumber = "12345678" }, "holder" ] do
            match! Api.createPaymentAsync client "/bank/accounts/ba.1/payments" (ToUkAccount account) (Money.gbp 1m) ValueNone ct with
            | Error(InvalidRequest reason) -> Assert.Contains(expected, reason)
            | other -> failwith $"%A{account}: %A{other}"

        Assert.Empty handler.Calls
    }

[<Fact>]
let ``an unusable BaseUrl or signature lifetime is InvalidRequest, not an exception`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> ok "{}")

        let badBase = { clientWith handler with Config = { config with BaseUrl = "not a url" } }

        match! Api.getIndexAsync badBase ct with
        | Error(InvalidRequest reason) -> Assert.Contains("BaseUrl", reason)
        | other -> failwith $"%A{other}"

        let signer = { KeyId = "hs.1"; Sign = fun _ _ -> Task.FromResult(Array.zeroCreate 64) }

        for lifetime in [ TimeSpan.Zero; TimeSpan.FromSeconds 301.0 ] do
            let client =
                { clientWith handler with Config = { config with Signer = ValueSome signer; SignatureLifetime = lifetime } }

            match! Api.getIndexAsync client ct with
            | Error(InvalidRequest reason) -> Assert.Contains("SignatureLifetime", reason)
            | other -> failwith $"{lifetime}: %A{other}"

        Assert.Empty handler.Calls
    }

[<Fact>]
let ``canonicalLink and resolveUrl compare prefixes ordinally`` () =
    // A soft hyphen is ignorable under culture comparison, so a culture StartsWith would treat it as "/v0/...".
    let softHyphen = "­/v0/bank/accounts/ba.1"
    Assert.Equal(softHyphen, Api.canonicalLink softHyphen)

    match Api.resolveUrl config softHyphen with
    | Error(ForeignLink _) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``printing the config hides the API key`` () =
    let printed = $"%A{config}" + string config
    Assert.DoesNotContain("g-test-key-1", printed)
    Assert.Contains("api.griffin.com", printed)

[<Fact>]
let ``cancelling the caller token throws instead of returning an error`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> ok "{}")
        use cts = new CancellationTokenSource()
        do! cts.CancelAsync()

        let! _ =
            Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> Api.getIndexAsync (clientWith handler) cts.Token :> Task)

        ()
    }

[<Fact>]
let ``workflows read the required claim types of their legal person type`` () : Task =
    task {
        use handler =
            new FakeGriffinHandler(fun _ _ _ ->
                ok """{"links":{"prev":null,"next":null},"workflows":[{"display-name":"Individual - Account Owners","disabled?":false,"legal-person-type":"individual","workflow-url":"/workflows/wf.1","required-claim-types":{"individual":["contact-details","us-citizen"]}}]}""")

        let! page = Api.listWorkflowsAsync (clientWith handler) "/organizations/og.1/workflows" ct
        let workflow = Assert.Single (expectOk page).Items
        Assert.Equal<string list>([ "contact-details"; "us-citizen" ], workflow.RequiredClaimTypes)
        Assert.False workflow.Disabled
    }

[<Fact>]
let ``legal person parses the latest decision with decline reasons`` () : Task =
    task {
        use handler =
            new FakeGriffinHandler(fun _ _ _ ->
                ok """{"legal-person-url":"/legal-persons/lp.1","display-name":"Ada","legal-person-type":"individual","application-status":"declined","latest-decision":{"decision-outcome":"declined","decision-notes":"","verification-url":"/verifications/vn.1","decision-maker":"system","created-at":"2026-10-26T16:45:40.088Z","decline-reasons":[{"decline-reason-code":"applicant-under-16","decline-reason-title":"Too young","decline-reason-description":"Under 16","decline-reason-claim-types":["individual-identity"]}]}}""")

        let! person = Api.getLegalPersonAsync (clientWith handler) "/legal-persons/lp.1" ct
        let decision = (expectOk person).LatestDecision.Value
        Assert.Equal(DecisionOutcome.Declined, decision.Outcome)
        let reason = Assert.Single decision.DeclineReasons
        Assert.Equal("applicant-under-16", reason.Code)
        Assert.Equal<string list>([ "individual-identity" ], reason.ClaimTypes)
    }
