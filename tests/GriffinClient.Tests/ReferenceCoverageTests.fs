/// Operations and fields of Griffin's API reference (2026-07-09), with JSON shaped as its OpenAPI specification says.
module ReferenceCoverageTests

open System
open System.Net.Http
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

let private bodyOf (handler: FakeGriffinHandler) = (Assert.Single handler.Calls).Body.Value

let private hasProperty (name: string) (body: Text.Json.JsonElement) = fst (body.TryGetProperty name)

[<Fact>]
let ``a scheduled submission reads when processing can start`` () : Task =
    task {
        let json =
            """{"submission-url":"/submissions/ps.1","payment-url":"/payments/pm.1","account-url":"/bank/accounts/ba.1","submission-status":"scheduled","scheduled-at":"2026-11-23T17:00:00.000Z","unique-scheme-identifier":"ABC123","created-at":"2026-11-22T17:01:54.359Z","submission-scheme-information":{"payment-scheme":"fps","end-to-end-identification":"E2E"}}"""

        use handler = new FakeGriffinHandler(fun _ _ _ -> ok json)
        let! submission = Api.getSubmissionAsync (clientWith handler) "/submissions/ps.1" ct
        let submission = expectOk submission
        Assert.Equal(SubmissionStatus.Scheduled, submission.Status)
        Assert.Equal(ValueSome(DateTimeOffset(2026, 11, 23, 17, 0, 0, TimeSpan.Zero)), submission.ScheduledAt)
        Assert.Equal(ValueSome "ABC123", submission.UniqueSchemeIdentifier)
    }

[<Fact>]
let ``a submission that is not scheduled has no ScheduledAt`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> ok (submissionJson "processing"))
        let! submission = Api.getSubmissionAsync (clientWith handler) "/submissions/ps.1" ct
        Assert.Equal(ValueNone, (expectOk submission).ScheduledAt)
    }

[<Fact>]
let ``a Direct Debit collection reads its type, mandate and rejection`` () : Task =
    task {
        let json =
            """{"payment-url":"/payments/pm.1","payment-direction":"outbound-payment","payment-type":"direct-debit","payment-amount":{"currency":"GBP","value":"9.99"},"mandate-url":"/mandates/mn.1","payment-admissions-url":"/payments/pm.1/admissions","rejection-reason":"not ours","rejected-at":"2026-11-02T09:00:00Z","created-at":"2026-11-01T09:00:00Z"}"""

        use handler = new FakeGriffinHandler(fun _ _ _ -> ok (pageJson "payments" [ json ] ValueNone))
        let! page = Api.listPaymentsAsync (clientWith handler) "/mandates/mn.1/payments" ct
        let payment = Assert.Single (expectOk page).Items
        Assert.Equal(ValueSome PaymentType.DirectDebit, payment.PaymentType)
        Assert.Equal(ValueSome "/mandates/mn.1", payment.MandateUrl)
        Assert.Equal(ValueSome "/payments/pm.1/admissions", payment.AdmissionsUrl)
        Assert.Equal(ValueSome "not ours", payment.RejectionReason)
        Assert.True payment.RejectedAt.IsSome
    }

[<Fact>]
let ``a payment is rejected by a PATCH with its reason`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> 204, "")
        let! result = Api.rejectPaymentAsync (clientWith handler) "/payments/pm.1" "not ours" ct
        expectOk result
        let call = Assert.Single handler.Calls
        Assert.Equal("PATCH", call.Method)
        Assert.Equal("/payments/pm.1", call.Target)
        Assert.Equal("not ours", call.Body.Value.GetProperty("rejection-reason").GetString())
    }

[<Fact>]
let ``a savings account reads its type, interest and dates`` () : Task =
    task {
        let json =
            """{"account-url":"/bank/accounts/ba.1","display-name":"Pot","account-status":"open","bank-product-type":"savings-account","savings-type":"easy-access","primary-account":false,"controller-url":"/legal-persons/lp.own","accrued-interest":{"currency":"GBP","value":"1.25"},"opened-at":"2026-01-02T00:00:00Z"}"""

        use handler = new FakeGriffinHandler(fun _ _ _ -> ok json)
        let! account = Api.getBankAccountAsync (clientWith handler) "/bank/accounts/ba.1" ct
        let account = expectOk account
        Assert.Equal(ValueSome SavingsType.EasyAccess, account.SavingsType)
        Assert.Equal(ValueSome(Money.gbp 1.25m), account.AccruedInterest)
        Assert.Equal(ValueSome "/legal-persons/lp.own", account.ControllerUrl)
        Assert.False account.PrimaryAccount
        Assert.True account.OpenedAt.IsSome
        Assert.Equal(ValueNone, account.ClosedAt)
    }

[<Theory>]
[<InlineData("""{"notice-period-type":"instant-access"}""", 0)>]
[<InlineData("""{"notice-period-type":"calendar-days","days-of-notice":7,"payout-time":{}}""", 7)>]
[<InlineData("""{"notice-period-type":"same-or-next-day","cutoff-time":{},"payout-time":{}}""", -1)>]
let ``a product reads its notice period`` (period: string, days: int) : Task =
    task {
        let json =
            $$$"""{"bank-account-product-url":"/bank/products/bp.1","display-name":"Saver","bank-account-product-class":"savings","bank-account-product-accounts-url":"/bank/products/bp.1/accounts","notice-period":{{{period}}}}"""

        use handler = new FakeGriffinHandler(fun _ _ _ -> ok json)
        let! product = Api.getBankProductAsync (clientWith handler) "/bank/products/bp.1" ct

        let expected =
            match days with
            | 0 -> NoticePeriod.InstantAccess
            | -1 -> NoticePeriod.SameOrNextDay
            | days -> NoticePeriod.CalendarDays days

        Assert.Equal(ValueSome expected, (expectOk product).NoticePeriod)
    }

[<Fact>]
let ``a workflow is fetched with its description and rules`` () : Task =
    task {
        let json =
            """{"workflow-url":"/workflows/wf.1","display-name":"Company","description":"Onboards companies","legal-person-type":"corporation","disabled?":false,"required-claim-types":{"corporation":["uk-company-register"]},"workflow-rules":["allow-zero-directors"],"created-at":"2026-01-01T00:00:00Z"}"""

        use handler = new FakeGriffinHandler(fun _ _ _ -> ok json)
        let! workflow = Api.getWorkflowAsync (clientWith handler) "/workflows/wf.1" ct
        let workflow = expectOk workflow
        Assert.Equal(ValueSome "Onboards companies", workflow.Description)
        Assert.Equal<string list>([ "allow-zero-directors" ], workflow.Rules)
        Assert.Equal<string list>([ "uk-company-register" ], workflow.RequiredClaimTypes)
    }

[<Fact>]
let ``a legal person's history reads each kind of event, and keeps one it does not know`` () : Task =
    task {
        let events =
            [ """{"legal-person-history-event-type":"risk-rating-registered","timestamp":"2026-03-01T00:00:00Z","risk-rating":"high-risk","previous-risk-rating":"low-risk","notes":"reviewed"}"""
              """{"legal-person-history-event-type":"decision-created","timestamp":"2026-02-01T00:00:00Z","decision-outcome":"accepted","decision-notes":"fine","verification-url":"/verifications/vn.1","decision-maker":"system"}"""
              """{"legal-person-history-event-type":"verification-created","timestamp":"2026-01-01T00:00:00Z","verification-url":"/verifications/vn.1","workflow-url":"/workflows/wf.1"}"""
              """{"legal-person-history-event-type":"something-new","timestamp":"2026-01-01T00:00:00Z"}""" ]

        use handler = new FakeGriffinHandler(fun _ _ _ -> ok (pageJson "legal-person-history" events ValueNone))
        let person: LegalPerson =
            { Url = "/legal-persons/lp.1"
              DisplayName = "Ada"
              LegalPersonType = LegalPersonType.Individual
              LegalName = ValueNone
              Status = ValueNone
              StatusChangedAt = ValueNone
              ApplicationStatus = ValueNone
              LatestDecision = ValueNone
              ClaimsUrl = ValueNone
              VerificationsUrl = ValueNone
              DecisionsUrl = ValueNone
              PayeesUrl = ValueNone
              CreatedAt = ValueNone
              Raw = "{}" }

        let! page = Api.listLegalPersonHistoryAsync (clientWith handler) person.HistoryUrl ct
        let page = expectOk page
        Assert.Equal("/legal-persons/lp.1/history", (Assert.Single handler.Calls).Target)
        Assert.Empty page.Unparsed

        match page.Items |> List.map _.Kind with
        | [ LegalPersonHistoryKind.RiskRatingRegistered(rating, previous, notes)
            LegalPersonHistoryKind.DecisionCreated decision
            LegalPersonHistoryKind.VerificationCreated(verificationUrl, workflowUrl)
            LegalPersonHistoryKind.Other "something-new" ] ->
            Assert.Equal(RiskRating.High, rating)
            Assert.Equal(ValueSome RiskRating.Low, previous)
            Assert.Equal(ValueSome "reviewed", notes)
            Assert.Equal(DecisionOutcome.Accepted, decision.Outcome)
            Assert.Equal("/verifications/vn.1", verificationUrl)
            Assert.Equal(ValueSome "/workflows/wf.1", workflowUrl)
        | other -> failwith $"%A{other}"

        Assert.True page.Items.Head.Timestamp.IsSome
    }

[<Fact>]
let ``verification profiles read the subject, related parties and their claims`` () : Task =
    task {
        let json =
            """{"verification-subject-profile":{"display-name":"Acme","legal-person-url":"/legal-persons/lp.1","legal-person-type":"corporation","legal-name":"Acme Ltd","current-claims":[{"claim-type":"uk-company-register","entity-name":"Acme"}]},"verification-related-party-profiles":[{"display-name":"Ada","legal-person-url":"/legal-persons/lp.2","legal-person-type":"individual","current-claims":[]}]}"""

        use handler = new FakeGriffinHandler(fun _ _ _ -> ok json)
        let! profiles = Api.getVerificationProfilesAsync (clientWith handler) "/verifications/vn.1/profiles" ct
        let profiles = expectOk profiles
        Assert.Equal(ValueSome "Acme Ltd", profiles.Subject.LegalName)
        let claim = Assert.Single profiles.Subject.CurrentClaims
        Assert.Equal("uk-company-register", claim.ClaimType)
        Assert.Contains("entity-name", claim.Raw)
        Assert.Equal("Ada", (Assert.Single profiles.RelatedParties).DisplayName)
    }

[<Fact>]
let ``accounts open in an organization by kind`` () : Task =
    task {
        let opened (displayName: string voption) (account: NewBankAccount) =
            task {
                use handler = new FakeGriffinHandler(fun _ _ _ -> created (accountJson "/bank/accounts/ba.1" "opening"))
                let! result = Api.openAccountAsync (clientWith handler) "/organizations/og.1/bank/accounts" displayName account ct
                Assert.Equal(AccountStatus.Opening, (expectOk result).Status)
                Assert.Equal("/organizations/og.1/bank/accounts", (Assert.Single handler.Calls).Target)
                return bodyOf handler
            }

        let! embedded = opened (ValueSome "Ada's") (NewBankAccount.Embedded "/legal-persons/lp.1")
        Assert.Equal("embedded-account", embedded.GetProperty("bank-product-type").GetString())
        Assert.Equal("/legal-persons/lp.1", embedded.GetProperty("owner-url").GetString())
        Assert.Equal("Ada's", embedded.GetProperty("display-name").GetString())

        let! operational = opened ValueNone NewBankAccount.Operational
        Assert.Equal("operational-account", operational.GetProperty("bank-product-type").GetString())
        Assert.False(hasProperty "display-name" operational)

        let! easy = opened ValueNone (NewBankAccount.EasyAccessSavings "/legal-persons/lp.1")
        Assert.Equal("savings-account", easy.GetProperty("bank-product-type").GetString())
        Assert.Equal("easy-access", easy.GetProperty("savings-type").GetString())

        let! bareTrust = opened ValueNone NewBankAccount.BareTrustSavings
        Assert.Equal("bare-trust", bareTrust.GetProperty("savings-type").GetString())
        Assert.False(hasProperty "owner-url" bareTrust)

        let! pooled = opened ValueNone (NewBankAccount.ClientMoney(AccountHolding.Pooled ValueNone))
        Assert.Equal("client-money-account", pooled.GetProperty("bank-product-type").GetString())
        Assert.True(pooled.GetProperty("pooled-funds").GetBoolean())
        Assert.False(hasProperty "owner-url" pooled)

        let! dedicated = opened ValueNone (NewBankAccount.Safeguarding(AccountHolding.ForBeneficiary("/legal-persons/lp.2", ValueSome "/legal-persons/lp.1")))
        Assert.Equal("safeguarding-account", dedicated.GetProperty("bank-product-type").GetString())
        Assert.False(dedicated.GetProperty("pooled-funds").GetBoolean())
        Assert.Equal("/legal-persons/lp.2", dedicated.GetProperty("beneficiary-url").GetString())
        Assert.Equal("/legal-persons/lp.1", dedicated.GetProperty("owner-url").GetString())
    }

[<Fact>]
let ``updating a restriction sends only what changes, with its notes`` () : Task =
    task {
        let json =
            """{"bank-account-restriction-url":"/bank/account-restrictions/br.1","account-url":"/bank/accounts/ba.1","restriction-type":"block","restriction-status":"active"}"""

        use handler = new FakeGriffinHandler(fun _ _ _ -> ok json)

        let! restriction =
            Api.updateRestrictionAsync
                (clientWith handler)
                "/bank/account-restrictions/br.1/actions/update"
                (ValueSome RestrictionType.Block)
                ValueNone
                ValueNone
                "Escalated"
                ct

        Assert.Equal(ValueSome RestrictionType.Block, (expectOk restriction).RestrictionType)
        let body = bodyOf handler
        Assert.Equal("block", body.GetProperty("restriction-type").GetString())
        Assert.Equal("Escalated", body.GetProperty("notes").GetString())
        Assert.False(hasProperty "restriction-reason-code" body)
        Assert.False(hasProperty "restriction-legal-status" body)
    }

[<Fact>]
let ``a webhook's test status reads a finished delivery and one not yet made`` () : Task =
    task {
        use finished =
            new FakeGriffinHandler(fun _ _ _ ->
                ok """{"webhook-request-status":"complete","webhook-http-status":500,"webhook-error-code":"webhook-invalid-response","updated-at":"2026-11-01T00:00:00Z"}""")

        let! status = Api.getWebhookTestStatusAsync (clientWith finished) "/webhooks/wh.1/actions/test" ct
        let status = expectOk status
        Assert.Equal("complete", status.Status)
        Assert.Equal(ValueSome 500, status.HttpStatus)
        Assert.Equal(ValueSome "webhook-invalid-response", status.ErrorCode)

        use untested =
            new FakeGriffinHandler(fun _ _ _ -> ok """{"webhook-request-status":"default","updated-at":"2026-11-01T00:00:00Z"}""")

        let! status = Api.getWebhookTestStatusAsync (clientWith untested) "/webhooks/wh.1/actions/test" ct
        Assert.Equal(ValueNone, (expectOk status).HttpStatus)
    }

[<Fact>]
let ``the signature debug endpoint is called with the method to check`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> ok """{"message":"Signature verified","busy":false}""")
        let! check = Api.verifyRequestSignatureAsync (clientWith handler) HttpMethod.Put ct
        Assert.Equal("Signature verified", (expectOk check).Message)
        let call = Assert.Single handler.Calls
        Assert.Equal("PUT", call.Method)
        Assert.Equal("/security/message-signature/verify", call.Target)
    }

[<Fact>]
let ``an onboarding application posts the subject and related profiles with their roles`` () : Task =
    task {
        use handler =
            new FakeGriffinHandler(fun _ _ _ ->
                created """{"onboarding-application-url":"/onboarding/applications/oa.1","onboarding-application-status":"submitted"}""")

        let subject: OnboardingProfile =
            { DisplayName = "Acme"
              Claims = [ Claims.contactDetails "acme@example.com" ] }

        let director: RelatedProfile =
            { DisplayName = "Ada"
              Roles =
                [ SubjectRole.Director(ValueSome "https://example/officer")
                  SubjectRole.Psc(ValueSome "75", ValueNone)
                  SubjectRole.SeniorManager
                  SubjectRole.RoleTitles [ "CEO" ] ]
              Claims = [ Claims.nationality "GB" ] }

        let! application =
            Api.createOnboardingApplicationAsync
                (clientWith handler)
                "/organizations/og.1/onboarding/applications"
                "/workflows/wf.1"
                subject
                [ director ]
                ct

        let application = expectOk application
        Assert.Equal(OnboardingStatus.Submitted, application.Status)
        Assert.Equal(ValueNone, application.WorkflowUrl)

        let body = bodyOf handler
        Assert.Equal("/workflows/wf.1", body.GetProperty("workflow-url").GetString())
        Assert.Equal("Acme", body.GetProperty("subject-profile").GetProperty("display-name").GetString())
        Assert.Equal("contact-details", body.GetProperty("subject-profile").GetProperty("claims").Item(0).GetProperty("claim-type").GetString())

        let related = body.GetProperty("related-profiles").Item(0)
        let association = related.GetProperty("subject-association")
        Assert.Equal("https://example/officer", association.GetProperty("director").GetProperty("companies-house-url").GetString())
        Assert.Equal("75", association.GetProperty("psc").GetProperty("ownership-percent").GetString())
        Assert.False(hasProperty "companies-house-url" (association.GetProperty "psc"))
        Assert.Equal(Text.Json.JsonValueKind.Object, association.GetProperty("senior-manager").ValueKind)
        Assert.Equal("CEO", association.GetProperty("role-titles").Item(0).GetString())
    }

[<Fact>]
let ``an onboarding application reads each status`` () : Task =
    task {
        let read (json: string) =
            task {
                use handler = new FakeGriffinHandler(fun _ _ _ -> ok json)
                let! application = Api.getOnboardingApplicationAsync (clientWith handler) "/onboarding/applications/oa.1" ct
                return expectOk application
            }

        let shared status rest =
            $"""{{"onboarding-application-url":"/onboarding/applications/oa.1","workflow-url":"/workflows/wf.1","created-at":"2026-11-01T00:00:00Z","onboarding-application-status":"{status}"{rest}}}"""

        let parties = ""","legal-person-url":"/legal-persons/lp.1","verification-url":"/verifications/vn.1" """

        let! processing = read (shared "processing" parties)
        Assert.Equal(OnboardingStatus.Processing, processing.Status)
        Assert.Equal(ValueSome "/legal-persons/lp.1", processing.LegalPersonUrl)
        Assert.Equal(ValueSome "/verifications/vn.1", processing.VerificationUrl)

        let! errored = read (shared "errored" (parties + ""","error-reasons":["bad claim"] """))
        Assert.Equal(OnboardingStatus.Errored [ "bad claim" ], errored.Status)

        let! declined =
            read (
                shared
                    "complete"
                    (parties
                     + ""","decision-outcome":"declined","decline-reasons":[{"decline-reason-code":"applicant-under-16","decline-reason-title":"Too young","decline-reason-description":"Under 16"}] """)
            )

        match declined.Status with
        | OnboardingStatus.Complete(DecisionOutcome.Declined, [ reason ]) -> Assert.Equal("applicant-under-16", reason.Code)
        | other -> failwith $"%A{other}"

        let! newer = read (shared "something-new" "")
        Assert.Equal(OnboardingStatus.Other "something-new", newer.Status)
    }

[<Fact>]
let ``a created API key keeps its secret out of what gets printed and logged`` () : Task =
    task {
        let json =
            """{"api-key-url":"/api-keys/ak.1","api-key-name":"CI","api-key-live?":false,"organization-url":"/organizations/og.1","user-url":"/users/ur.1","created-at":"2026-11-01T00:00:00Z","api-key-secret":"g-test-very-secret"}"""

        use handler = new FakeGriffinHandler(fun _ _ _ -> created json)
        let! created = Api.createApiKeyAsync (clientWith handler) "/organizations/og.1/api-keys" "CI" ct
        let created = expectOk created
        Assert.Equal("g-test-very-secret", created.Secret)
        Assert.Equal("CI", created.Key.Name)
        Assert.Equal("CI", (bodyOf handler).GetProperty("api-key-name").GetString())

        for printed in [ string created; $"%A{created}"; $"%O{created}"; created.Key.Raw ] do
            Assert.DoesNotContain("very-secret", printed)
    }

[<Fact>]
let ``API keys are read, listed and deleted`` () : Task =
    task {
        let key =
            """{"api-key-url":"/api-keys/ak.1","api-key-name":"CI","api-key-live?":true,"organization-url":"/organizations/og.1","user-url":"/users/ur.1"}"""

        use handler =
            new FakeGriffinHandler(fun method target _ ->
                match method, target with
                | "DELETE", _ -> 204, ""
                | _, "/organizations/og.1/api-keys" -> ok (pageJson "api-keys" [ key ] ValueNone)
                | _ -> ok key)

        let client = clientWith handler
        let! got = Api.getApiKeyAsync client "/api-keys/ak.1" ct
        Assert.True (expectOk got).Live
        let! listed = Api.listApiKeysAsync client "/organizations/og.1/api-keys" ct
        Assert.Equal("/users/ur.1", (Assert.Single (expectOk listed).Items).UserUrl)
        let! deleted = Api.deleteApiKeyAsync client "/api-keys/ak.1" ct
        expectOk deleted
        Assert.Equal("DELETE", (List.last handler.Calls).Method)
    }

let private role (url: string) =
    $"""{{"role-url":"{url}","display-name":"Viewer","description":"Can view","role-category":"general"}}"""

let private user =
    """{"user-url":"/users/ur.1","user-email":"ada@example.com","user-username":"ada","given-name":"Ada","api-keys-url":"/users/ur.1/api-keys","user-memberships-url":"/users/ur.1/memberships"}"""

[<Fact>]
let ``users and roles are read`` () : Task =
    task {
        use handler =
            new FakeGriffinHandler(fun _ target _ ->
                match target with
                | "/users/ur.1" -> ok user
                | "/roles" -> ok $"""{{"roles":[{role "/roles/ro.1"}]}}"""
                | _ -> ok (role "/roles/ro.1"))

        let client = clientWith handler
        let! got = Api.getUserAsync client "/users/ur.1" ct
        let got = expectOk got
        Assert.Equal("ada@example.com", got.Email)
        Assert.Equal(ValueSome "/users/ur.1/api-keys", got.ApiKeysUrl)
        Assert.Equal(ValueNone, got.Surname)
        let! roles = Api.listRolesAsync client "/roles" ct
        Assert.Equal("Viewer", (Assert.Single (expectOk roles)).DisplayName)
        let! one = Api.getRoleAsync client "/roles/ro.1" ct
        Assert.Equal(ValueSome "general", (expectOk one).Category)
    }

[<Fact>]
let ``memberships are read, their roles replaced, and members removed`` () : Task =
    task {
        let membership =
            $$$"""{"membership-url":"/memberships/mb.1","membership-roles-url":"/memberships/mb.1/roles","organization":{"organization-url":"/organizations/og.1"},"user":{{{user}}},"roles":[{{{role "/roles/ro.1"}}}],"created-at":"2026-01-01T00:00:00Z"}"""

        use handler =
            new FakeGriffinHandler(fun method target _ ->
                match method, target with
                | "DELETE", _ -> 204, ""
                | _, "/organizations/og.1/memberships" -> ok (pageJson "memberships" [ membership ] ValueNone)
                | _, "/memberships/mb.1/roles" -> ok $"""{{"roles":[{role "/roles/ro.2"}]}}"""
                | _ -> ok membership)

        let client = clientWith handler
        let! listed = Api.listMembershipsAsync client "/organizations/og.1/memberships" ct
        let listed = Assert.Single (expectOk listed).Items
        Assert.Equal(ValueSome "/organizations/og.1", listed.OrganizationUrl)
        Assert.Equal(ValueSome "ada", listed.User |> ValueOption.map _.Username)
        Assert.Equal("/roles/ro.1", (Assert.Single listed.Roles).Url)

        let! one = Api.getMembershipAsync client "/memberships/mb.1" ct
        Assert.Equal("/memberships/mb.1/roles", (expectOk one).RolesUrl)

        let! current = Api.listMembershipRolesAsync client listed.RolesUrl ct
        Assert.Equal("/roles/ro.2", (Assert.Single (expectOk current)).Url)

        let! replaced = Api.setMembershipRolesAsync client listed.RolesUrl [ "/roles/ro.2" ] ct
        Assert.Equal("/roles/ro.2", (Assert.Single (expectOk replaced)).Url)
        let put = List.last handler.Calls
        Assert.Equal("PUT", put.Method)
        Assert.Equal("/roles/ro.2", put.Body.Value.GetProperty("role-urls").Item(0).GetString())

        let! removed = Api.deleteMembershipAsync client "/memberships/mb.1" ct
        expectOk removed
        Assert.Equal("DELETE", (List.last handler.Calls).Method)
    }

[<Fact>]
let ``an invitation posts the email address and has no answer to read`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> 201, "")
        let! result = Api.createInvitationAsync (clientWith handler) "/organizations/og.1/invitations" "ada@example.com" ct
        expectOk result
        Assert.Equal("ada@example.com", (bodyOf handler).GetProperty("email-address").GetString())
    }

[<Fact>]
let ``the index and organization read the team and onboarding links`` () : Task =
    task {
        let index =
            """{"organization-url":"/organizations/og.1","users-url":"/users","roles-url":"/roles","api-key-url":"/api-keys/ak.1","organizations-url":"/organizations"}"""

        let organization =
            organizationJson.TrimEnd('}')
            + ""","organization-memberships-url":"/m","organization-invitations-url":"/i","organization-api-keys-url":"/k","organization-onboarding-applications-url":"/o"}"""

        use handler =
            new FakeGriffinHandler(fun _ target _ ->
                match target with
                | "/index" -> ok index
                | _ -> ok organization)

        let client = clientWith handler
        let! read = Api.getIndexAsync client ct
        let read = expectOk read
        Assert.Equal(ValueSome "/roles", read.RolesUrl)
        Assert.Equal(ValueSome "/users", read.UsersUrl)
        Assert.Equal(ValueSome "/api-keys/ak.1", read.ApiKeyUrl)

        let! own = Api.getOwnOrganizationAsync client ct
        let own = expectOk own
        Assert.Equal(ValueSome "/m", own.MembershipsUrl)
        Assert.Equal(ValueSome "/i", own.InvitationsUrl)
        Assert.Equal(ValueSome "/k", own.ApiKeysUrl)
        Assert.Equal(ValueSome "/o", own.OnboardingApplicationsUrl)
    }

let private payloadOf (eventType: string) (payload: string) =
    match Event.parse $"""{{"event-url":"/events/ev.1","event-type":"{eventType}","event-payload":{payload}}}""" with
    | Ok event -> event.Payload
    | Error failure -> failwith (GriffinFailure.describe failure)

[<Fact>]
let ``onboarding, verification resource and test events are typed`` () =
    let application =
        """{"onboarding-application-url":"/onboarding/applications/oa.1","onboarding-application-status":"processing","workflow-url":"/workflows/wf.1"}"""

    match payloadOf "onboarding-application-created" application with
    | OnboardingApplicationCreated created -> Assert.Equal(OnboardingStatus.Processing, created.Status)
    | other -> failwith $"%A{other}"

    match payloadOf "onboarding-application-updated" application with
    | OnboardingApplicationUpdated updated -> Assert.Equal(ValueSome "/workflows/wf.1", updated.WorkflowUrl)
    | other -> failwith $"%A{other}"

    let resource =
        """{"verification-resource-type":"idv-check","verification-resource-id":"vr.1","verification-url":"/verifications/vn.1","organization-url":"/organizations/og.1","idv-check-status":"complete","idv-check-url":"https://example/idv","idv-check-expires-at":"2026-12-01"}"""

    match payloadOf "verification-resource-updated" resource with
    | VerificationResourceUpdated updated ->
        Assert.Equal(ValueSome "vr.1", updated.ResourceId)
        Assert.Equal(ValueSome IdvCheckStatus.Complete, updated.Status)
        Assert.Equal(ValueSome "https://example/idv", updated.IdvCheckUrl)
        Assert.True updated.ExpiresAt.IsSome
    | other -> failwith $"%A{other}"

    match payloadOf "verification-resource-created" """{"verification-url":"/verifications/vn.1","legal-person-url":"/legal-persons/lp.1"}""" with
    | VerificationResourceCreated created -> Assert.Equal(ValueSome "/legal-persons/lp.1", created.LegalPersonUrl)
    | other -> failwith $"%A{other}"

    Assert.Equal(TestEvent, payloadOf "test-event" "{}")
