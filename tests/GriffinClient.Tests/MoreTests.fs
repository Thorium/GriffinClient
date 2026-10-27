module MoreTests

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
    | Error e -> failwith (GriffinFailure.describe e)

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

[<Fact>]
let ``searches post the query and read search-results`` () : Task =
    task {
        use handler =
            new FakeGriffinHandler(fun _ target _ ->
                match target with
                | "/organizations/og.1/payments/search" -> ok $"""{{"search-results":[{paymentJson}]}}"""
                | "/organizations/og.1/bank/accounts/search" ->
                    ok $"""{{"search-results":[{accountJson "/bank/accounts/ba.1" "open"}],"meta":{{"page":{{"total":1}}}}}}"""
                | _ -> ok """{"search-results":[{"legal-person-url":"/legal-persons/lp.1","display-name":"Ada","legal-person-type":"individual"}]}""")

        let client = clientWith handler
        let! payments = Api.searchPaymentsAsync client organization.PaymentsSearchUrl "Rent" ct
        Assert.Equal(Money.gbp 25.5m, (Assert.Single (expectOk payments).Items).Amount)
        let! accounts = Api.searchBankAccountsAsync client organization.BankAccountsSearchUrl "00330292" ct
        Assert.Equal(AccountStatus.Open, (Assert.Single (expectOk accounts).Items).Status)
        let! people = Api.searchLegalPersonsAsync client organization.LegalPersonsSearchUrl "Ada" ct
        Assert.Equal("Ada", (Assert.Single (expectOk people).Items).DisplayName)

        Assert.Equal<string list>(
            [ "/organizations/og.1/payments/search"; "/organizations/og.1/bank/accounts/search"; "/organizations/og.1/legal-persons/search" ],
            handler.Calls |> List.map _.Target
        )

        Assert.Equal("Rent", handler.Calls[0].Body.Value.GetProperty("search-query").GetString())
    }

[<Fact>]
let ``a company lookup reads Companies House details`` () : Task =
    task {
        // Shape of GET /companies-house/companies/{number} in the sandbox, 2026-10-27, trimmed.
        let json =
            """{"date-of-latest-accounts":"2026-02-28","accounts-overdue":false,"persons-with-significant-control":[{"display-name":"Ada Example","given-name":"Ada","surname":"Example","natures-of-control":["ownership-of-shares-75-to-100-percent"],"month-of-birth":6,"year-of-birth":1985,"companies-house-url":"https://example/psc"}],"corporation-type":"public-limited-company","entity-name":"TESCO PLC","entity-registration-number":"00445790","directors":[{"month-of-birth":9,"year-of-birth":1974,"director-appointed-on":"2018-09-24","display-name":"BETHELL, Melissa","companies-house-url":"https://example/officer"}],"confirmation-statement-overdue":false,"date-of-incorporation":"1947-11-27","sic-codes":["47110"],"date-of-latest-confirmation-statement":"2026-06-18","company-address":{"street-name":"Tesco House, Shire Park","city":"Welwyn Garden City","postal-code":"AL7 1GA","country-code":"GB"},"company-status":"active"}"""

        use handler = new FakeGriffinHandler(fun _ _ _ -> ok json)
        let! company = Api.lookupCompanyAsync (clientWith handler) "00445790" ct
        let company = expectOk company
        Assert.Equal("/companies-house/companies/00445790", (Assert.Single handler.Calls).Target)
        Assert.Equal("TESCO PLC", company.Name)
        Assert.Equal(ValueSome(DateTime(1947, 11, 27)), company.IncorporatedOn)
        Assert.Equal(ValueSome "Welwyn Garden City", company.Address |> ValueOption.bind _.City)
        let director = Assert.Single company.Directors
        Assert.Equal(ValueSome(DateTime(2018, 9, 24)), director.AppointedOn)
        Assert.Equal(ValueSome 1974, director.BirthYear)
        let psc = Assert.Single company.PersonsWithSignificantControl
        Assert.Equal<string list>([ "ownership-of-shares-75-to-100-percent" ], psc.NaturesOfControl)
    }

[<Fact>]
let ``holds list from the account's holds link`` () : Task =
    task {
        let holdJson =
            """{"hold-url":"/bank/holds/hd.1","account-url":"/bank/accounts/ba.1","amount":{"currency":"GBP","value":"12.00"},"direction":"debit","status":"released","origin-type":"card-transaction","release-reason":"cleared","created-at":"2026-10-27T10:00:00Z","updated-at":"2026-10-27T11:00:00Z","account-transactions":[{"account-transaction-url":"/bank/transactions/tr.9","updated-hold-at":"2026-10-27T11:00:00Z"}]}"""

        use handler =
            new FakeGriffinHandler(fun _ target _ ->
                match target with
                | "/bank/accounts/ba.1" -> ok (accountJson "/bank/accounts/ba.1" "open")
                | _ -> ok (pageJson "holds" [ holdJson ] ValueNone))

        let client = clientWith handler
        let! account = Api.getBankAccountAsync client "/bank/accounts/ba.1" ct
        let! page = Api.listHoldsAsync client (expectOk account).HoldsUrl ct
        let hold = Assert.Single (expectOk page).Items
        Assert.Equal("/bank/accounts/ba.1/holds", handler.Calls[1].Target)
        Assert.Equal(HoldStatus.Released, hold.Status)
        Assert.Equal(BalanceDirection.Debit, hold.Direction)
        Assert.Equal<string list>([ "/bank/transactions/tr.9" ], hold.TransactionUrls)
    }

[<Fact>]
let ``pool membership is listed and updated by legal person URL`` () : Task =
    task {
        use handler =
            new FakeGriffinHandler(fun method _ _ ->
                match method with
                | "GET" -> ok """{"pool-members":[{"legal-person-url":"/legal-persons/lp.1","created-at":"2026-10-27T10:00:00Z"}],"links":{"prev":null,"next":null}}"""
                | _ -> ok "{}")

        let client = clientWith handler
        let! _ = Api.updatePoolMembersAsync client "/bank/accounts/ba.1/membership-updates" [ "/legal-persons/lp.1" ] [] ct
        let! members = Api.listPoolMembersAsync client "/bank/accounts/ba.1/membership" ct
        Assert.Equal("/legal-persons/lp.1", (Assert.Single (expectOk members).Items).LegalPersonUrl)
        let body = handler.Calls[0].Body.Value
        Assert.Equal("/legal-persons/lp.1", (body.GetProperty "additions").[0].GetString())
        Assert.Equal(0, body.GetProperty("deletions").GetArrayLength())
    }

[<Fact>]
let ``a tell consent registers with its permissions and accounts, and revokes`` () : Task =
    task {
        let consentJson status =
            $"""{{"tell-consent-url":"/open-banking/tell-consents/tc.1","tell-consent-id":"c-1","tell-gateway-id":"g-1","tell-consent-scope":"accounts","tell-consent-status":"{status}","tell-consent-permissions":["ReadBalances"],"bank-account-urls":["/bank/accounts/ba.1"],"created-at":"2026-10-27T10:00:00Z"}}"""

        use handler =
            new FakeGriffinHandler(fun _ target _ ->
                if target.EndsWith("/actions/revoke", StringComparison.Ordinal) then ok (consentJson "revoked")
                else created (consentJson "authorised"))

        let client = clientWith handler

        let request =
            { ConsentId = "c-1"
              GatewayId = "g-1"
              Scope = TellConsentScope.Accounts
              Permissions = [ "ReadBalances" ]
              BankAccountUrls = [ "/bank/accounts/ba.1" ]
              Expiry = ValueNone }

        let! consent = Api.registerTellConsentAsync client organization.TellConsentsUrl request ct
        let consent = expectOk consent
        Assert.Equal(TellConsentScope.Accounts, consent.Scope)
        let! revoked = Api.revokeTellConsentAsync client consent.RevokeUrl ct
        Assert.Equal("revoked", (expectOk revoked).Status)

        let body = handler.Calls[0].Body.Value
        Assert.Equal("/organizations/og.1/open-banking/tell-consents", handler.Calls[0].Target)
        Assert.Equal("accounts", body.GetProperty("tell-consent-scope").GetString())
        Assert.Equal("ReadBalances", (body.GetProperty "tell-consent-permissions").[0].GetString())
        Assert.False(body.TryGetProperty "tell-consent-expiry" |> fst)
        Assert.Equal("/open-banking/tell-consents/tc.1/actions/revoke", handler.Calls[1].Target)
    }

[<Fact>]
let ``account rename and webhook description use PATCH`` () : Task =
    task {
        let webhookJson =
            """{"webhook-url":"/webhooks/wh.1","webhook-destination-url":"https://example.com/h","webhook-description":"new","webhook-state":"active"}"""

        use handler =
            new FakeGriffinHandler(fun _ target _ ->
                if target.StartsWith("/webhooks", StringComparison.Ordinal) then ok webhookJson
                else ok (accountJson "/bank/accounts/ba.1" "open"))

        let client = clientWith handler
        let! _ = Api.updateBankAccountAsync client "/bank/accounts/ba.1" "Renamed" ct
        let! hook = Api.updateWebhookAsync client "/webhooks/wh.1" "new" ct
        Assert.Equal(WebhookState.Active, (expectOk hook).State)
        Assert.Equal<string list>([ "PATCH"; "PATCH" ], handler.Calls |> List.map _.Method)
        Assert.Equal("Renamed", handler.Calls[0].Body.Value.GetProperty("display-name").GetString())
        Assert.Equal("new", handler.Calls[1].Body.Value.GetProperty("webhook-description").GetString())
    }

[<Fact>]
let ``the verified-bank-account claim carries Griffin's uk-domestic field names`` () =
    let json = Text.Json.JsonSerializer.Serialize(Claim.toJson (Claims.verifiedBankAccount "600130" "12345678"))
    let e = Text.Json.JsonDocument.Parse(json).RootElement
    Assert.Equal("verified-bank-account", e.GetProperty("claim-type").GetString())
    Assert.Equal("600130", e.GetProperty("uk-domestic-sort-code").GetString())
    Assert.Equal("12345678", e.GetProperty("uk-domestic-account-number").GetString())
