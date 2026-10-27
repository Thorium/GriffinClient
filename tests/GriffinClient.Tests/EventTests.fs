module EventTests

open Xunit
open Griffin
open FakeGriffin

let private event (eventType: string) (payload: string) =
    $"""{{"event-url":"/events/ev.1","event-type":"{eventType}","event-payload":{payload},"created-at":"2026-10-26T16:46:14.033Z"}}"""

let private payloadOf json =
    match Event.parse json with
    | Ok e -> e.Payload
    | Error e -> failwith $"%A{e}"

[<Fact>]
let ``transaction-created carries a typed transaction, also with v0 links`` () =
    let payload = (transactionJson "tr.1" "12.34" "credit").Replace("\"/bank/", "\"/v0/bank/")

    match payloadOf (event "transaction-created" payload) with
    | TransactionCreated t ->
        Assert.Equal(Money.gbp 12.34m, t.BalanceChange)
        Assert.Equal("/bank/accounts/ba.1", Api.canonicalLink t.AccountUrl)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``account status and submission events are typed`` () =
    Assert.Equal(
        AccountStatusUpdated("/bank/accounts/ba.1", AccountStatus.Open),
        payloadOf (event "account-status-updated" """{"account-url":"/bank/accounts/ba.1","account-status":"open"}""")
    )

    match payloadOf (event "submission-updated" (submissionJson "delivered")) with
    | SubmissionUpdated s -> Assert.Equal(SubmissionStatus.Delivered, s.Status)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a payload newer than the request version is Unavailable with the version it needs`` () =
    let payload =
        """{"title":"Unavailable","detail":"Newer","meta":{"current":{"api-version":"2026-07-09"},"required":{"api-version":"2027-01-01"}}}"""

    Assert.Equal(Unavailable "2027-01-01", payloadOf (event "transaction-created" payload))

[<Fact>]
let ``unknown event types and unparseable known payloads become Other`` () =
    match payloadOf (event "onboarding-application-created" """{"x":1}""") with
    | Other("onboarding-application-created", json) -> Assert.Contains("\"x\"", json)
    | other -> failwith $"%A{other}"

    match payloadOf (event "submission-updated" """{"unexpected":true}""") with
    | Other("submission-updated", _) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``an event without event-type is an error`` () =
    Assert.True(Result.isError (Event.parse """{"event-url":"/events/ev.1"}"""))
    Assert.True(Result.isError (Event.parse "not json"))
