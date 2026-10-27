module BankingTests

open System
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

let private organization: Organization =
    { Url = "/organizations/og.1"
      DisplayName = "Test Org"
      Mode = OrganizationMode.TestMode
      OwnLegalPersonUrl = ValueSome "/legal-persons/lp.own"
      LegalPersonsUrl = "/organizations/og.1/legal-persons"
      BankAccountsUrl = "/organizations/og.1/bank/accounts"
      BankProductsUrl = "/organizations/og.1/bank/products"
      WorkflowsUrl = "/organizations/og.1/workflows"
      PaymentsUrl = ValueNone
      EventsUrl = ValueNone
      WebhooksUrl = ValueNone
      Raw = "{}" }

let private verification: Verification =
    { Url = "/verifications/vn.1"
      LegalPersonUrl = "/legal-persons/lp.1"
      WorkflowUrl = ValueNone
      Status = VerificationStatus.ChecksComplete
      RiskRating = ValueNone
      ResourcesUrl = ValueNone
      CreatedAt = ValueNone
      Raw = "{}" }

let private tuomas =
    { AccountHolder = "Tuomas Test"
      SortCode = "600130"
      AccountNumber = "12345678" }

[<Fact>]
let ``bank accounts carry the admissions, submissions, restrictions, statements and mandates links`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> ok (accountJson "/bank/accounts/ba.1" "open"))
        let! account = Api.getBankAccountAsync (clientWith handler) "/bank/accounts/ba.1" ct
        let account = expectOk account
        Assert.Equal(ValueSome "/bank/accounts/ba.1/admissions", account.AdmissionsUrl)
        Assert.Equal(ValueSome "/bank/accounts/ba.1/submissions", account.SubmissionsUrl)
        Assert.Equal(ValueSome "/bank/accounts/ba.1/restrictions", account.RestrictionsUrl)
        Assert.Equal(ValueSome "/bank/accounts/ba.1/statements", account.StatementsUrl)
        Assert.Equal(ValueSome "/bank/accounts/ba.1/mandates", account.MandatesUrl)
    }

[<Fact>]
let ``the IDV check link comes from the verification resources`` () : Task =
    task {
        // Shape of GET /verifications/{id}/resources in the sandbox, 2026-10-27.
        let json =
            """{"links":{"prev":null,"next":null},"verification-url":"/verifications/vn.1","legal-person-url":"/legal-persons/lp.1","organization-url":"/organizations/og.1","verification-resources":[{"idv-check-url":"https://idv.example/check/1","legal-person-url":"/legal-persons/lp.1","idv-check-expires-at":"2026-10-26","idv-check-status":"pending","display-name":"Ada Example","verification-resource-type":"idv-check","verification-resource-id":"vr.1"}]}"""

        use handler = new FakeGriffinHandler(fun _ _ _ -> ok json)
        let! checks = Api.getIdvChecksAsync (clientWith handler) "/verifications/vn.1/resources" ct
        let check = Assert.Single(expectOk checks)
        Assert.Equal(IdvCheckStatus.Pending, check.Status)
        Assert.Equal("https://idv.example/check/1", check.Url)
        Assert.Equal(ValueSome(DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero)), check.ExpiresAt)
    }

[<Fact>]
let ``a manual decision posts the verification, outcome and notes`` () : Task =
    task {
        use handler =
            new FakeGriffinHandler(fun _ _ _ ->
                created """{"decision-outcome":"declined","decision-notes":"Proof of address missing","verification-url":"/verifications/vn.1","legal-person-url":"/legal-persons/lp.1","decision-maker":"user","created-at":"2026-10-27T10:00:00Z"}""")

        let! decision =
            Api.createDecisionAsync (clientWith handler) "/legal-persons/lp.1/decisions" verification DecisionOutcome.Declined "Proof of address missing" ct

        Assert.Equal(ValueSome DecisionMaker.User, (expectOk decision).DecisionMaker)
        let body = (Assert.Single handler.Calls).Body.Value
        Assert.Equal("/verifications/vn.1", body.GetProperty("verification-url").GetString())
        Assert.Equal("declined", body.GetProperty("decision-outcome").GetString())
        Assert.Equal("Proof of address missing", body.GetProperty("decision-notes").GetString())
    }

[<Fact>]
let ``an error reply without a body is ApiError with no errors`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> 422, "")

        match! Api.createDecisionAsync (clientWith handler) "/legal-persons/lp.1/decisions" verification DecisionOutcome.Accepted "" ct with
        | Error(ApiError(422, [])) -> ()
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``claims list, delete and legal person rename use the right verbs`` () : Task =
    task {
        use handler =
            new FakeGriffinHandler(fun method _ _ ->
                match method with
                | "GET" ->
                    ok
                        """{"claims":[{"us-citizen?":false,"claim-type":"us-citizen","created-at":"2026-10-26T17:24:11.802Z","claim-url":"/claims/cl.1"}],"links":{"prev":null,"next":null}}"""
                | "DELETE" -> 204, ""
                | _ -> ok """{"legal-person-url":"/legal-persons/lp.1","display-name":"Ada Q Example","legal-person-type":"individual"}""")

        let client = clientWith handler
        let! claims = Api.listClaimsAsync client "/legal-persons/lp.1/claims" ct
        let claim = Assert.Single (expectOk claims).Items
        Assert.Equal("us-citizen", claim.ClaimType)
        Assert.Contains("\"us-citizen?\":false", claim.Raw)

        let! deleted = Api.deleteClaimAsync client claim.Url ct
        Assert.Equal(Ok(), deleted)
        let! renamed = Api.updateLegalPersonAsync client "/legal-persons/lp.1" "Ada Q Example" ct
        Assert.Equal("Ada Q Example", (expectOk renamed).DisplayName)

        Assert.Equal<(string * string) list>(
            [ "GET", "/legal-persons/lp.1/claims"; "DELETE", "/claims/cl.1"; "PUT", "/legal-persons/lp.1" ],
            handler.Calls |> List.map (fun c -> c.Method, c.Target)
        )

        Assert.Equal("Ada Q Example", handler.Calls[2].Body.Value.GetProperty("display-name").GetString())
    }

let private copJson =
    """{"cop-request-url":"/cop-request/cp.1","account":{"account-number":"12345678","bank-id":"600130","account-classification":"personal","name":"Tuomas Test","requester-legal-person-url":"/legal-persons/lp.1"},"cop-response":{"status":"unverified","result":"close-match","reason":"close-match-name","suggested-name":"Tuomas Test CloseMatch"}}"""

[<Fact>]
let ``Confirmation of Payee posts to the organization and reads a close match with the suggested name`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> created copJson)
        let client = clientWith handler

        let! check =
            Api.confirmPayeeAsync client organization "/legal-persons/lp.1" tuomas AccountClassification.Personal ValueNone ct

        let check = expectOk check
        Assert.Equal("/cop-request/cp.1", check.Url)
        Assert.Equal(CopResult.CloseMatch, check.Result)
        Assert.Equal(CopStatus.Unverified, check.Status)
        Assert.Equal(ValueSome CopReason.CloseMatchName, check.Reason)
        Assert.Equal(ValueSome "Tuomas Test CloseMatch", check.SuggestedName)

        let! _ = Api.simulateCopAsync client organization CopSimulation.FullMatch "/legal-persons/lp.1" tuomas AccountClassification.Business ct

        match handler.Calls with
        | [ real; simulated ] ->
            Assert.Equal("/organizations/og.1/cop-request", real.Target)
            let body = real.Body.Value
            Assert.Equal("/legal-persons/lp.1", body.GetProperty("requester-legal-person-url").GetString())
            Assert.Equal("personal", body.GetProperty("account-classification").GetString())
            Assert.Equal("600130", body.GetProperty("bank-id").GetString())
            Assert.False(body.TryGetProperty "secondary-reference-data" |> fst)
            Assert.Equal("/simulation/organizations/og.1/cop-request/simulate/full-match", simulated.Target)
            Assert.Equal("business", simulated.Body.Value.GetProperty("account-classification").GetString())
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``a payee records its CoP check and deactivates with a PATCH`` () : Task =
    task {
        let payeeJson status =
            $"""{{"payee-url":"/payees/pe.1","legal-person-url":"/legal-persons/lp.1","account-holder":"Tuomas Test","account-number":"12345678","bank-id":"600130","country-code":"GB","payee-status":"{status}","cop-request-url":"/cop-request/cp.1","created-at":"2026-10-27T10:00:00Z"}}"""

        use handler =
            new FakeGriffinHandler(fun method _ _ ->
                match method with
                | "PATCH" -> ok (payeeJson "deactivated")
                | _ -> created (payeeJson "active"))

        let client = clientWith handler
        let! payee = Api.createPayeeAsync client "/legal-persons/lp.1/bank/payees" tuomas (ValueSome "/cop-request/cp.1") ct
        let payee = expectOk payee
        Assert.Equal(ValueSome "/cop-request/cp.1", payee.CopRequestUrl)
        Assert.Equal(tuomas, payee.Account)
        let! deactivated = Api.deactivatePayeeAsync client "/payees/pe.1" ct
        Assert.Equal(PayeeStatus.Deactivated, (expectOk deactivated).Status)

        match handler.Calls with
        | [ create; patch ] ->
            Assert.Equal("/cop-request/cp.1", create.Body.Value.GetProperty("cop-request-url").GetString())
            Assert.Equal("PATCH", patch.Method)
            Assert.Equal("deactivated", patch.Body.Value.GetProperty("payee-status").GetString())
        | other -> failwith $"%A{other}"
    }

let private restrictionJson status =
    $"""{{"bank-account-restriction-url":"/bank/account-restrictions/br.1","account-url":"/bank/accounts/ba.1","restriction-type":"freeze","restriction-status":"{status}","restriction-reason-code":"customer-request","restriction-legal-status":"not-applicable","lift-bank-account-restriction-url":"/bank/account-restrictions/br.1/actions/lift","update-bank-account-restriction-url":"/bank/account-restrictions/br.1/actions/update","bank-account-restriction-events-url":"/bank/account-restrictions/br.1/events","restriction-applied-by":{{"restriction-applied-by-type":"user","user-url":"/users/ur.1"}},"created-at":"2026-10-27T10:00:00Z"}}"""

[<Fact>]
let ``a customer-requested freeze is applied, listed and lifted`` () : Task =
    task {
        use handler =
            new FakeGriffinHandler(fun method target _ ->
                match method, target with
                | "GET", _ -> ok (pageJson "bank-account-restrictions" [ restrictionJson "active" ] ValueNone)
                | "POST", t when t.EndsWith "/lift" -> ok (restrictionJson "lifted")
                | _ -> created (restrictionJson "active"))

        let client = clientWith handler

        let! freeze =
            Api.applyRestrictionAsync
                client
                "/bank/accounts/ba.1/restrictions"
                RestrictionType.Freeze
                RestrictionReason.CustomerRequest
                RestrictionLegalStatus.NotApplicable
                "Cool-off"
                ct

        let freeze = expectOk freeze
        Assert.Equal(RestrictionStatus.Active, freeze.Status)
        Assert.Equal(ValueSome RestrictionType.Freeze, freeze.RestrictionType)
        Assert.Equal(ValueSome RestrictionReason.CustomerRequest, freeze.Reason)
        let! listed = Api.listRestrictionsAsync client "/bank/accounts/ba.1/restrictions" ct
        Assert.Single (expectOk listed).Items |> ignore
        let! lifted = Api.liftRestrictionAsync client freeze.LiftUrl.Value "Cool-off over" ct
        Assert.Equal(RestrictionStatus.Lifted, (expectOk lifted).Status)

        let apply = handler.Calls[0].Body.Value
        Assert.Equal("freeze", apply.GetProperty("restriction-type").GetString())
        Assert.Equal("customer-request", apply.GetProperty("restriction-reason-code").GetString())
        Assert.Equal("not-applicable", apply.GetProperty("restriction-legal-status").GetString())
        Assert.Equal("Cool-off", apply.GetProperty("notes").GetString())
        Assert.Equal("Cool-off over", handler.Calls[2].Body.Value.GetProperty("notes").GetString())
    }

[<Fact>]
let ``a statement request sends format and dates, and its download link is fetched with the API key`` () : Task =
    task {
        let statementJson =
            """{"statement-url":"/bank/statements/st.1","account-url":"/bank/accounts/ba.1","format":"xero-csv","start-date":"2026-09-27","end-date":"2026-10-26","status":"generated","created-at":"2026-10-27T10:00:00Z","statement-download-url":"/bank/statements/st.1/download"}"""

        use handler =
            new FakeGriffinHandler(fun _ target _ ->
                match target with
                | "/bank/statements/st.1/download" -> 200, "Date,Amount\n2026/10/26,25.50\n"
                | _ -> created statementJson)

        let client = clientWith handler

        let! statement =
            Api.requestStatementAsync client "/bank/accounts/ba.1/statements" StatementFormat.XeroCsv (DateTime(2026, 9, 27)) (DateTime(2026, 10, 26)) ct

        let statement = expectOk statement
        Assert.Equal(StatementStatus.Generated, statement.Status)
        Assert.Equal(StatementFormat.XeroCsv, statement.Format)
        let! bytes = Api.downloadAsync client statement.DownloadUrl.Value ct
        Assert.StartsWith("Date,Amount", Text.Encoding.UTF8.GetString(expectOk bytes))

        match handler.Calls with
        | [ request; download ] ->
            let body = request.Body.Value
            Assert.Equal("xero-csv", body.GetProperty("format").GetString())
            Assert.Equal("2026-09-27", body.GetProperty("start-date").GetString())
            Assert.Equal("2026-10-26", body.GetProperty("end-date").GetString())
            Assert.Equal("GriffinAPIKey g-test-key-1", download.Headers["Authorization"])
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``a download link on another host is ForeignLink and nothing is fetched`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> 200, "%PDF-1.7")
        let client = clientWith handler

        for link in [ "https://files.example/statement.pdf?sig=abc"; "http://files.example/statement.pdf" ] do
            match! Api.downloadAsync client link ct with
            | Error(ForeignLink l) -> Assert.Equal(link, l)
            | other -> failwith $"%s{link}: %A{other}"

        Assert.Empty handler.Calls
    }

[<Fact>]
let ``mandates parse fixed and variable amounts, and cancel with a PATCH`` () : Task =
    task {
        let mandate (url: string) (status: string) (amount: string) =
            $"""{{"mandate-url":"{url}","status":"{status}","reference":"BET-123","collection-frequency":"monthly","collection-amount":{amount},"creditor":{{"creditor-type":"uk-domestic","account-holder":"Some Bookmaker Ltd","bank-id":"600130","account-number":"12345678"}},"debtor":{{"account-url":"/bank/accounts/ba.1"}},"mandate-payments-url":"{url}/payments","mandate-scheme-information":{{"payment-scheme":"bacs"}},"created-at":"2026-10-01T09:00:00Z"}}"""

        let fixedAmount = """{"amount-type":"fixed","amount":{"currency":"GBP","value":"20.00"}}"""
        let variableAmount = """{"amount-type":"variable","max-amount":{"currency":"GBP","value":"500.00"}}"""

        use handler =
            new FakeGriffinHandler(fun method _ _ ->
                match method with
                | "PATCH" -> 204, ""
                | _ -> ok (pageJson "mandates" [ mandate "/mandates/md.1" "active" fixedAmount; mandate "/mandates/md.2" "active" variableAmount ] ValueNone))

        let client = clientWith handler
        let! page = Api.listMandatesAsync client "/bank/accounts/ba.1/mandates" ct

        match (expectOk page).Items with
        | [ fixedMandate; variableMandate ] ->
            Assert.Equal(ValueSome(Fixed(Money.gbp 20m)), fixedMandate.Amount)
            Assert.Equal(ValueSome CollectionFrequency.Monthly, fixedMandate.Frequency)
            Assert.Equal(MandateStatus.Active, fixedMandate.Status)
            Assert.Equal(ValueSome(Variable(ValueSome(Money.gbp 500m))), variableMandate.Amount)
            Assert.Equal(ValueSome "Some Bookmaker Ltd", variableMandate.CreditorName)
            Assert.Equal(ValueSome "/bank/accounts/ba.1", variableMandate.AccountUrl)
        | other -> failwith $"%A{other}"

        let! cancelled = Api.cancelMandateAsync client "/mandates/md.2" MandateCancellationReason.CancelledByDebtor ct
        Assert.Equal(Ok(), cancelled)
        let patch = handler.Calls[1]
        Assert.Equal("PATCH", patch.Method)
        Assert.Equal("cancelled", patch.Body.Value.GetProperty("status").GetString())
        Assert.Equal("cancelled-by-debtor", patch.Body.Value.GetProperty("cancellation-reason").GetString())
    }

[<Fact>]
let ``admissions list the inbound payments of an account`` () : Task =
    task {
        // Shape of GET /bank/accounts/{id}/admissions in the sandbox, 2026-10-27.
        let admission =
            """{"account-url":"/bank/accounts/ba.1","admission-status":"delivered","created-at":"2026-10-26T17:24:18.808Z","admission-scheme-information":{"payment-scheme":"book-transfer","end-to-end-identification":"E2E"},"unique-scheme-identifier":"USI","admission-url":"/admissions/pa.1","payment-url":"/payments/pm.1"}"""

        use handler = new FakeGriffinHandler(fun _ _ _ -> ok (pageJson "admissions" [ admission ] ValueNone))
        let! page = Api.listAdmissionsAsync (clientWith handler) "/bank/accounts/ba.1/admissions" ct
        let a = Assert.Single (expectOk page).Items
        Assert.Equal(AdmissionStatus.Delivered, a.Status)
        Assert.Equal(ValueSome PaymentScheme.BookTransfer, a.Scheme)
        Assert.Equal("/payments/pm.1", a.PaymentUrl)
    }
