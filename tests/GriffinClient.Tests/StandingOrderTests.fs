module StandingOrderTests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Griffin
open FakeGriffin

let private ct = CancellationToken.None

let private order: StandingOrder =
    { Reference = "Rent"
      Amount = Money.gbp 500m
      Creditor = ToPayee "/payees/pe.1"
      Scheme = PaymentScheme.Fps
      FirstDate = DateTime(2026, 1, 31)
      Frequency = OrderFrequency.EveryMonths(1, MonthDay.Day 31)
      End = OrderEnd.NoEnd
      NonWorkingDays = NonWorkingDayRule.PayOnTheDay }

let private dates (occurrences: seq<OrderOccurrence>) =
    occurrences |> Seq.map (fun o -> o.ScheduledDate.ToString "yyyy-MM-dd") |> List.ofSeq

let private first count (order: StandingOrder) =
    StandingOrder.occurrences WorkingCalendar.weekdays order |> Seq.truncate count

[<Fact>]
let ``a day a month lacks falls on its last day, and the next month goes back to the chosen day`` () =
    Assert.Equal<string list>(
        [ "2026-01-31"; "2026-02-28"; "2026-03-31"; "2026-04-30"; "2026-05-31" ],
        dates (first 5 order)
    )

[<Fact>]
let ``February 29 counts in a leap year`` () =
    let leap = { order with FirstDate = DateTime(2028, 1, 30); Frequency = OrderFrequency.EveryMonths(1, MonthDay.Day 30) }
    Assert.Equal<string list>([ "2028-01-30"; "2028-02-29"; "2028-03-30" ], dates (first 3 leap))

[<Fact>]
let ``the end of the month is each month's last day`` () =
    let endOfMonth = { order with FirstDate = DateTime(2026, 1, 15); Frequency = OrderFrequency.EveryMonths(1, MonthDay.Last) }
    Assert.Equal<string list>([ "2026-01-31"; "2026-02-28"; "2026-03-31" ], dates (first 3 endOfMonth))

[<Fact>]
let ``a first date after the month's chosen day starts the next month`` () =
    let later = { order with FirstDate = DateTime(2026, 1, 20); Frequency = OrderFrequency.EveryMonths(1, MonthDay.Day 15) }
    Assert.Equal<string list>([ "2026-02-15"; "2026-03-15" ], dates (first 2 later))

[<Fact>]
let ``weekly, fortnightly, quarterly and yearly orders repeat from the first date`` () =
    let every frequency = { order with FirstDate = DateTime(2026, 1, 5); Frequency = frequency }
    Assert.Equal<string list>([ "2026-01-05"; "2026-01-12"; "2026-01-19" ], dates (first 3 (every (OrderFrequency.EveryWeeks 1))))
    Assert.Equal<string list>([ "2026-01-05"; "2026-01-19" ], dates (first 2 (every (OrderFrequency.EveryWeeks 2))))
    Assert.Equal<string list>([ "2026-01-05"; "2026-04-05" ], dates (first 2 (every (OrderFrequency.EveryMonths(3, MonthDay.Day 5)))))
    Assert.Equal<string list>([ "2026-01-05"; "2027-01-05" ], dates (first 2 (every (OrderFrequency.EveryMonths(12, MonthDay.Day 5)))))

[<Fact>]
let ``a one-off order pays once on its date`` () =
    let once = { order with FirstDate = DateTime(2026, 3, 2); Frequency = OrderFrequency.Once }
    Assert.Equal<string list>([ "2026-03-02" ], dates (StandingOrder.occurrences WorkingCalendar.weekdays once))

[<Fact>]
let ``an order ends after its count or last date`` () =
    let weekly = { order with FirstDate = DateTime(2026, 1, 5); Frequency = OrderFrequency.EveryWeeks 1 }
    let counted = { weekly with End = OrderEnd.Count 2 }
    Assert.Equal<string list>([ "2026-01-05"; "2026-01-12" ], dates (StandingOrder.occurrences WorkingCalendar.weekdays counted))
    let dated = { weekly with End = OrderEnd.LastDate(DateTime(2026, 1, 12)) }
    Assert.Equal<string list>([ "2026-01-05"; "2026-01-12" ], dates (StandingOrder.occurrences WorkingCalendar.weekdays dated))

[<Fact>]
let ``non-working days move the due date, not the scheduled date or reference`` () =
    // 2026-03-01 is a Sunday.
    let sunday = { order with FirstDate = DateTime(2026, 3, 1); Frequency = OrderFrequency.Once }
    let occurrenceBy rule =
        StandingOrder.occurrences WorkingCalendar.weekdays { sunday with NonWorkingDays = rule } |> Seq.head

    Assert.Equal(DateTime(2026, 3, 1), (occurrenceBy NonWorkingDayRule.PayOnTheDay).Due)
    Assert.Equal(DateTime(2026, 3, 2), (occurrenceBy NonWorkingDayRule.NextWorkingDay).Due)
    Assert.Equal(DateTime(2026, 2, 27), (occurrenceBy NonWorkingDayRule.PreviousWorkingDay).Due)
    Assert.Equal(DateTime(2026, 3, 1), (occurrenceBy NonWorkingDayRule.NextWorkingDay).ScheduledDate)
    Assert.Equal("Rent 20260301", (occurrenceBy NonWorkingDayRule.NextWorkingDay).Reference)

[<Fact>]
let ``bank holidays come from the caller's calendar`` () =
    // Christmas Day and Boxing Day 2026 fall on a Friday and a Saturday; the substitute holiday is the Monday.
    let calendar =
        WorkingCalendar.withHolidays [ DateTime(2026, 12, 25); DateTime(2026, 12, 28) ]

    let christmas =
        { order with
            FirstDate = DateTime(2026, 12, 25)
            Frequency = OrderFrequency.Once
            NonWorkingDays = NonWorkingDayRule.NextWorkingDay }

    Assert.Equal(DateTime(2026, 12, 29), (StandingOrder.occurrences calendar christmas |> Seq.head).Due)

[<Fact>]
let ``a calendar with no working days is refused rather than looped over`` () =
    let closed = { IsWorkingDay = fun _ -> false }
    let moved = { order with NonWorkingDays = NonWorkingDayRule.NextWorkingDay }
    Assert.Throws<ArgumentException>(fun () -> StandingOrder.occurrences closed moved |> Seq.head |> ignore) |> ignore

[<Fact>]
let ``due occurrences are those not yet completed, including any missed, oldest first`` () =
    let monthly = { order with FirstDate = DateTime(2026, 1, 15); Frequency = OrderFrequency.EveryMonths(1, MonthDay.Day 15) }
    let due completed today = StandingOrder.dueOccurrences WorkingCalendar.weekdays completed today monthly |> dates
    Assert.Empty(due ValueNone (DateTime(2026, 1, 14)))
    Assert.Equal<string list>([ "2026-01-15" ], due ValueNone (DateTime(2026, 1, 15)))
    Assert.Empty(due (ValueSome(DateTime(2026, 1, 15))) (DateTime(2026, 2, 14)))
    Assert.Equal<string list>([ "2026-02-15"; "2026-03-15" ], due (ValueSome(DateTime(2026, 1, 15))) (DateTime(2026, 3, 20)))

[<Fact>]
let ``an occurrence moved to a working day is due on that day`` () =
    // 2026-03-01 is a Sunday; paid on the Friday before.
    let early =
        { order with
            FirstDate = DateTime(2026, 3, 1)
            Frequency = OrderFrequency.Once
            NonWorkingDays = NonWorkingDayRule.PreviousWorkingDay }

    Assert.Equal<string list>([ "2026-03-01" ], StandingOrder.dueOccurrences WorkingCalendar.weekdays ValueNone (DateTime(2026, 2, 27)) early |> dates)
    Assert.Empty(StandingOrder.dueOccurrences WorkingCalendar.weekdays ValueNone (DateTime(2026, 2, 26)) early)

[<Fact>]
let ``the next due date is after the given day, and none is left after the end`` () =
    let counted = { order with FirstDate = DateTime(2026, 1, 5); Frequency = OrderFrequency.EveryWeeks 1; End = OrderEnd.Count 2 }
    let next after = StandingOrder.nextDue WorkingCalendar.weekdays after counted |> ValueOption.map _.ScheduledDate
    Assert.Equal(ValueSome(DateTime(2026, 1, 5)), next (DateTime(2026, 1, 1)))
    Assert.Equal(ValueSome(DateTime(2026, 1, 12)), next (DateTime(2026, 1, 5)))
    Assert.Equal(ValueNone, next (DateTime(2026, 1, 12)))

[<Fact>]
let ``orders that could not run are refused with the reason`` () =
    let refused (bad: StandingOrder) = Assert.True(Result.isError (StandingOrder.validate bad), $"%A{bad}")
    Assert.Equal(Ok order, StandingOrder.validate order)
    refused { order with Reference = " " }
    refused { order with Reference = String('r', StandingOrder.ReferenceLimit + 1) }
    Assert.True(Result.isOk (StandingOrder.validate { order with Reference = String('r', StandingOrder.ReferenceLimit) }))
    refused { order with Amount = Money.gbp 0m }
    refused { order with Amount = Money.gbp 1.005m }
    refused { order with Amount = { Currency = "EUR"; Value = 5m } }
    refused { order with Amount = Money.gbp 1000000.01m }
    refused { order with Scheme = PaymentScheme.Chaps }
    refused { order with Scheme = PaymentScheme.Bacs }
    refused { order with Frequency = OrderFrequency.EveryWeeks 0 }
    refused { order with Frequency = OrderFrequency.EveryMonths(0, MonthDay.Last) }
    refused { order with Frequency = OrderFrequency.EveryMonths(1, MonthDay.Day 32) }
    refused { order with End = OrderEnd.Count 0 }
    refused { order with End = OrderEnd.LastDate(order.FirstDate.AddDays -1.0) }
    refused { order with Creditor = ToUkAccount { AccountHolder = "Bob"; SortCode = "1"; AccountNumber = "2" } }

[<Fact>]
let ``the longest allowed reference and its date fit Griffin's 35 characters`` () =
    let longest = { order with Reference = String('r', StandingOrder.ReferenceLimit) }
    Assert.Equal(35, (first 1 longest |> Seq.head).Reference.Length)

/// Griffin for one account: payments and their submissions, with faults at chosen steps.
type private FakeAccount() =
    let payments = ResizeArray<string * string * bool>()
    member val CreateCalls = 0 with get, set
    member val SubmitCalls = 0 with get, set
    member val Submitted = 0 with get, set
    /// The next create fails: true after Griffin stored the payment, false before.
    member val FailCreate: bool voption = ValueNone with get, set
    /// The next submit fails: true after Griffin accepted it, false before.
    member val FailSubmit: bool voption = ValueNone with get, set
    member val SubmitRefusal: string voption = ValueNone with get, set
    member _.Payments = payments

    member private _.Json(url: string, reference: string, submitted: bool) =
        let latest = if submitted then $""","latest-submission-url":"{url}/submission" """ else ""
        $"""{{"payment-url":"{url}","payment-direction":"outbound-payment","payment-amount":{{"currency":"GBP","value":"500.00"}},"payment-reference":"{reference}","payment-submissions-url":"{url}/submissions"{latest}}}"""

    member private _.Submission(url: string) =
        $"""{{"submission-url":"{url}/submission","payment-url":"{url}","submission-status":"processing"}}"""

    member this.Respond (method: string) (target: string) (body: JsonElement voption) =
        let store () =
            let url = $"/payments/pm.{payments.Count + 1}"
            payments.Add(url, body.Value.GetProperty("payment-reference").GetString(), false)
            url

        match method, target with
        | "GET", t when t.StartsWith "/bank/accounts/ba.1/payments" ->
            ok (pageJson "payments" [ for url, reference, submitted in payments -> this.Json(url, reference, submitted) ] ValueNone)
        | "POST", "/bank/accounts/ba.1/payments" ->
            this.CreateCalls <- this.CreateCalls + 1

            match this.FailCreate with
            | ValueSome committed ->
                this.FailCreate <- ValueNone
                if committed then store () |> ignore
                500, ""
            | ValueNone ->
                let url = store ()
                let _, reference, _ = payments[payments.Count - 1]
                created (this.Json(url, reference, false))
        | "POST", t when t.EndsWith "/submissions" ->
            this.SubmitCalls <- this.SubmitCalls + 1
            let url = t.Replace("/submissions", "")
            let index = payments.FindIndex(fun (u, _, _) -> u = url)
            let u, reference, submitted = payments[index]

            match this.SubmitRefusal with
            | ValueSome code ->
                422, $"""{{"errors":[{{"title":"Unprocessable","status":"422","code":"{code}"}}]}}"""
            | ValueNone when submitted -> 410, """{"errors":[{"title":"Gone","status":"410"}]}"""
            | ValueNone ->
                match this.FailSubmit with
                | ValueSome committed ->
                    this.FailSubmit <- ValueNone

                    if committed then
                        payments[index] <- (u, reference, true)
                        this.Submitted <- this.Submitted + 1

                    500, ""
                | ValueNone ->
                    payments[index] <- (u, reference, true)
                    this.Submitted <- this.Submitted + 1
                    created (this.Submission url)
        | "GET", t when t.EndsWith "/submission" -> ok (this.Submission(t.Replace("/submission", "")))
        | "GET", t when t.EndsWith "/submissions" -> ok (pageJson "submissions" [ this.Submission(t.Replace("/submissions", "")) ] ValueNone)
        | "GET", t ->
            let url, reference, submitted = payments.Find(fun (u, _, _) -> u = t)
            ok (this.Json(url, reference, submitted))
        | _ -> 404, ""

let private occurrence = first 1 order |> Seq.head

let private run (account: FakeAccount) =
    task {
        use handler = new FakeGriffinHandler(fun method target body -> account.Respond method target body)
        return! StandingOrder.payAsync (clientWith handler) "/bank/accounts/ba.1/payments" order occurrence ct
    }

[<Fact>]
let ``an occurrence is created with its reference and submitted`` () : Task =
    task {
        let account = FakeAccount()
        use handler = new FakeGriffinHandler(fun method target body -> account.Respond method target body)
        let! outcome = StandingOrder.payAsync (clientWith handler) "/bank/accounts/ba.1/payments" order occurrence ct

        match outcome with
        | Ok(Submitted(payment, submission)) ->
            Assert.Equal(ValueSome "Rent 20260131", payment.Reference)
            Assert.Equal(SubmissionStatus.Processing, submission.Status)
        | other -> failwith $"%A{other}"

        let lookup = handler.Calls.Head
        Assert.Equal("GET", lookup.Method)
        Assert.Contains("filter%5Bcreated-at%5D%5Bgte%5D=2026-01-30T00%3A00%3A00Z", lookup.Target)
        Assert.Contains("filter%5Bpayment-direction%5D%5Beq%5D=outbound-payment", lookup.Target)
        Assert.Contains("page%5Bsize%5D=200", lookup.Target)
        Assert.Equal("fps", (List.last handler.Calls).Body.Value.GetProperty("payment-scheme").GetString())
    }

[<Fact>]
let ``running a paid occurrence again pays nothing more`` () : Task =
    task {
        let account = FakeAccount()
        let! _ = run account
        let! again = run account

        match again with
        | Ok(AlreadySubmitted(payment, _)) -> Assert.Equal("/payments/pm.1", payment.Url)
        | other -> failwith $"%A{other}"

        Assert.Equal(1, account.CreateCalls)
        Assert.Equal(1, account.SubmitCalls)
        Assert.Equal(1, account.Submitted)
    }

/// Runs until the occurrence reports a submission.
let rec private settle (account: FakeAccount) (attempts: int) : Task<unit> =
    task {
        match! run account with
        | Ok(Submitted _)
        | Ok(AlreadySubmitted _) -> return ()
        | _ when attempts > 1 -> return! settle account (attempts - 1)
        | other -> return failwith $"%A{other}"
    }

/// Every fault a run can meet, followed by runs until the occurrence reports a submission, and one more.
[<Theory>]
[<InlineData("create fails, not stored")>]
[<InlineData("create fails, stored")>]
[<InlineData("submit fails, not accepted")>]
[<InlineData("submit fails, accepted")>]
let ``whatever fails, repeated runs pay once`` (fault: string) : Task =
    task {
        let account = FakeAccount()

        match fault with
        | "create fails, not stored" -> account.FailCreate <- ValueSome false
        | "create fails, stored" -> account.FailCreate <- ValueSome true
        | "submit fails, not accepted" -> account.FailSubmit <- ValueSome false
        | _ -> account.FailSubmit <- ValueSome true

        do! settle account 5
        let! _ = run account
        Assert.Equal(1, account.Submitted)
        Assert.Equal(1, account.Payments.Count)
    }

[<Fact>]
let ``a create without a clear answer says so, and the next run finds the payment`` () : Task =
    task {
        let account = FakeAccount(FailCreate = ValueSome true)
        let! first = run account

        match first with
        | Ok(CreationUnknown(ApiError(500, _))) -> ()
        | other -> failwith $"%A{other}"

        let! second = run account

        match second with
        | Ok(Submitted _) -> ()
        | other -> failwith $"%A{other}"

        Assert.Equal(1, account.CreateCalls)
    }

[<Fact>]
let ``a refused submission reads as its reason, and the same payment goes once the cause is fixed`` () : Task =
    task {
        let account = FakeAccount(SubmitRefusal = ValueSome "insufficient-funds")
        let! refused = run account

        match refused with
        | Error failure -> Assert.Equal(ValueSome PaymentRefusal.InsufficientFunds, PaymentRefusal.ofFailure failure)
        | other -> failwith $"%A{other}"

        account.SubmitRefusal <- ValueNone
        let! paid = run account

        match paid with
        | Ok(Submitted _) -> ()
        | other -> failwith $"%A{other}"

        Assert.Equal(1, account.CreateCalls)
        Assert.Equal(1, account.Submitted)
    }

[<Fact>]
let ``a rejected payment is not paid or created again`` () : Task =
    task {
        let rejected =
            $"""{{"payment-url":"/payments/pm.1","payment-direction":"outbound-payment","payment-amount":{{"currency":"GBP","value":"500.00"}},"payment-reference":"{occurrence.Reference}","payment-submissions-url":"/payments/pm.1/submissions","rejected-at":"2026-02-01T00:00:00Z"}}"""

        use handler = new FakeGriffinHandler(fun _ _ _ -> ok (pageJson "payments" [ rejected ] ValueNone))
        let! outcome = StandingOrder.payAsync (clientWith handler) "/bank/accounts/ba.1/payments" order occurrence ct

        match outcome with
        | Ok(Rejected payment) -> Assert.True payment.RejectedAt.IsSome
        | other -> failwith $"%A{other}"

        Assert.Single handler.Calls |> ignore
    }

/// A payment listed without its submission, as a list lagging behind Griffin could show it, which answers a submit with
/// 410 and reads with `current` added.
let private lagging (current: string) =
    let payment (extra: string) =
        $"""{{"payment-url":"/payments/pm.1","payment-direction":"outbound-payment","payment-amount":{{"currency":"GBP","value":"500.00"}},"payment-reference":"{occurrence.Reference}","payment-submissions-url":"/payments/pm.1/submissions"{extra}}}"""

    new FakeGriffinHandler(fun method target _ ->
        match method, target with
        | "POST", "/payments/pm.1/submissions" -> 410, """{"errors":[{"title":"Gone","status":"410"}]}"""
        | "GET", "/payments/pm.1" -> ok (payment current)
        | "GET", "/payments/pm.1/submission" ->
            ok """{"submission-url":"/payments/pm.1/submission","payment-url":"/payments/pm.1","submission-status":"delivered"}"""
        | "GET", "/payments/pm.1/submissions" ->
            ok (pageJson "submissions" [ """{"submission-url":"/payments/pm.1/submission","payment-url":"/payments/pm.1","submission-status":"processing"}""" ] ValueNone)
        | _ -> ok (pageJson "payments" [ payment "" ] ValueNone))

[<Fact>]
let ``a submit answering 410 reads the submission Griffin already has`` () : Task =
    task {
        use handler = lagging """,  "latest-submission-url":"/payments/pm.1/submission" """
        let! outcome = StandingOrder.payAsync (clientWith handler) "/bank/accounts/ba.1/payments" order occurrence ct

        match outcome with
        | Ok(AlreadySubmitted(_, submission)) -> Assert.Equal(SubmissionStatus.Delivered, submission.Status)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``a submit answering 410 finds the submission in the list when the payment has no link to it`` () : Task =
    task {
        use handler = lagging ""
        let! outcome = StandingOrder.payAsync (clientWith handler) "/bank/accounts/ba.1/payments" order occurrence ct

        match outcome with
        | Ok(AlreadySubmitted(_, submission)) -> Assert.Equal(SubmissionStatus.Processing, submission.Status)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``a submit answering 410 because the payment was rejected reports the rejection`` () : Task =
    task {
        use handler = lagging """,  "rejected-at":"2026-02-01T00:00:00Z" """
        let! outcome = StandingOrder.payAsync (clientWith handler) "/bank/accounts/ba.1/payments" order occurrence ct

        match outcome with
        | Ok(Rejected _) -> ()
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``an unreadable payment in the lookup stops the run instead of risking a second one`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> ok (pageJson "payments" [ """{"payment-url":"/payments/pm.1"}""" ] ValueNone))
        let! outcome = StandingOrder.payAsync (clientWith handler) "/bank/accounts/ba.1/payments" order occurrence ct

        match outcome with
        | Error(InvalidResponse reason) -> Assert.Contains("could not be read", reason)
        | other -> failwith $"%A{other}"

        Assert.Single handler.Calls |> ignore
    }

[<Fact>]
let ``a reference Griffin changed the case or spacing of still identifies the payment`` () : Task =
    task {
        let reshaped =
            $"""{{"payment-url":"/payments/pm.1","payment-direction":"outbound-payment","payment-amount":{{"currency":"GBP","value":"500.00"}},"payment-reference":" {occurrence.Reference.ToUpperInvariant()} ","payment-submissions-url":"/payments/pm.1/submissions","latest-submission-url":"/payments/pm.1/submission"}}"""

        use handler =
            new FakeGriffinHandler(fun _ target _ ->
                if target.EndsWith "/submission" then
                    ok """{"submission-url":"/payments/pm.1/submission","payment-url":"/payments/pm.1","submission-status":"delivered"}"""
                else
                    ok (pageJson "payments" [ reshaped ] ValueNone))

        let! outcome = StandingOrder.payAsync (clientWith handler) "/bank/accounts/ba.1/payments" order occurrence ct

        match outcome with
        | Ok(AlreadySubmitted(_, submission)) -> Assert.Equal(SubmissionStatus.Delivered, submission.Status)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``an invalid order sends nothing`` () : Task =
    task {
        use handler = new FakeGriffinHandler(fun _ _ _ -> ok "{}")
        let bad = { order with Scheme = PaymentScheme.Chaps }
        let! outcome = StandingOrder.payAsync (clientWith handler) "/bank/accounts/ba.1/payments" bad occurrence ct
        Assert.True(Result.isError outcome)
        Assert.Empty handler.Calls
    }

[<Theory>]
[<InlineData("account-restricted")>]
[<InlineData("payee-not-active")>]
[<InlineData("same-debtor-and-beneficiary")>]
[<InlineData("mixed-currencies")>]
[<InlineData("mixed-organizations")>]
[<InlineData("payment-amount-exceeds-scheme-limit")>]
[<InlineData("bank-account-closed")>]
[<InlineData("insufficient-funds")>]
[<InlineData("payee-deactivated")>]
[<InlineData("no-postal-address")>]
[<InlineData("profile-not-found")>]
[<InlineData("cop-required")>]
let ``every documented refusal has its own case, read from the code or title`` (code: string) =
    let error code title : GriffinError =
        { Title = title
          Detail = ValueNone
          Code = code
          SourcePointer = ValueNone
          About = ValueNone }

    let byCode = PaymentRefusal.ofFailure (ApiError(422, [ error (ValueSome code) "Unprocessable" ]))
    let byTitle = PaymentRefusal.ofFailure (ApiError(422, [ error ValueNone code ]))
    Assert.Equal(byCode, byTitle)

    match byCode with
    | ValueSome(PaymentRefusal.Other other) -> failwith $"{other} is not typed"
    | ValueSome refusal -> Assert.Equal(code, refusal.Value)
    | ValueNone -> failwith "not read"

[<Fact>]
let ``only a 422 is a refusal`` () =
    Assert.Equal(ValueNone, PaymentRefusal.ofFailure (ApiError(500, [])))
    Assert.Equal(ValueNone, PaymentRefusal.ofFailure (ApiError(422, [])))
    Assert.Equal(ValueNone, PaymentRefusal.ofFailure (InvalidRequest "no"))

[<Fact>]
let ``a product says what each scheme can do`` () : Task =
    task {
        let json =
            """{"bank-account-product-url":"/bank/products/bp.1","display-name":"Current","bank-account-product-class":"embedded","bank-account-product-accounts-url":"/a","payment-schemes":{"faster-payments":{"inbound":true,"outbound":true},"chaps":{"inbound":true},"bacs-direct-credits":{"inbound":false},"bacs-direct-debits":{"inbound":true}}}"""

        use handler = new FakeGriffinHandler(fun _ _ _ -> ok json)
        let! product = Api.getBankProductAsync (clientWith handler) "/bank/products/bp.1" ct

        match product with
        | Ok { Schemes = ValueSome schemes } ->
            Assert.True schemes.FasterPaymentsOutbound
            Assert.True schemes.ChapsInbound
            Assert.False schemes.BacsDirectCreditsInbound
            Assert.True schemes.BacsDirectDebitsInbound
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``a check is attached to an existing payee`` () : Task =
    task {
        let json =
            """{"payee-url":"/payees/pe.1","legal-person-url":"/legal-persons/lp.1","account-holder":"Bob","bank-id":"000000","account-number":"12345678","payee-status":"active","cop-request-url":"/cop-request/cp.1"}"""

        use handler = new FakeGriffinHandler(fun _ _ _ -> ok json)
        let! payee = Api.attachCopToPayeeAsync (clientWith handler) "/payees/pe.1" "/cop-request/cp.1" ct

        match payee with
        | Ok read -> Assert.Equal(ValueSome "/cop-request/cp.1", read.CopRequestUrl)
        | Error failure -> failwith (GriffinFailure.describe failure)

        let call = Assert.Single handler.Calls
        Assert.Equal("PATCH", call.Method)
        Assert.Equal("/cop-request/cp.1", call.Body.Value.GetProperty("cop-request-url").GetString())
    }
