namespace Griffin

open System
open System.Globalization
open System.Threading
open System.Threading.Tasks

/// Which days are working days. Griffin publishes no calendar, so bank holidays come from the caller, for example from
/// https://www.gov.uk/bank-holidays.json, which differs between England and Wales, Scotland and Northern Ireland.
type WorkingCalendar = { IsWorkingDay: DateTime -> bool }

module WorkingCalendar =

    /// Monday to Friday, with no bank holidays.
    let weekdays =
        { IsWorkingDay =
            fun day -> day.DayOfWeek <> DayOfWeek.Saturday && day.DayOfWeek <> DayOfWeek.Sunday }

    /// Monday to Friday, except holidays.
    let withHolidays (holidays: seq<DateTime>) =
        let closed = holidays |> Seq.map _.Date |> Set.ofSeq

        { IsWorkingDay = fun day -> weekdays.IsWorkingDay day && not (closed.Contains day.Date) }

/// The day of the month a monthly order pays.
[<RequireQualifiedAccess>]
type MonthDay =
    /// 1 to 31. In a month too short for it, the order pays on that month's last day, and returns to this day after.
    | Day of int
    | Last

[<RequireQualifiedAccess>]
type OrderFrequency =
    /// One payment on the first date: a future-dated payment.
    | Once
    /// Every this many weeks from the first date: 1 weekly, 2 fortnightly.
    | EveryWeeks of int
    /// Every this many months from the first date's month: 1 monthly, 3 quarterly, 12 yearly.
    | EveryMonths of months: int * day: MonthDay

[<RequireQualifiedAccess>]
type OrderEnd =
    | NoEnd
    /// No occurrence is scheduled after this date.
    | LastDate of DateTime
    /// This many occurrences in all.
    | Count of int

/// What a date on a non-working day does. Faster Payments and book transfers run every day, so this is a choice.
[<RequireQualifiedAccess>]
type NonWorkingDayRule =
    | PayOnTheDay
    | NextWorkingDay
    | PreviousWorkingDay

/// A repeating payment the caller keeps and runs: Griffin has no standing orders, and nothing pays unless the caller's
/// job runs. Dates are UK calendar dates; any time of day on them is ignored.
type StandingOrder =
    { /// At most ReferenceLimit characters, and unique among the orders of an account: with the date it identifies a
      /// payment, so two orders with one reference would count as one.
      Reference: string
      Amount: Money
      Creditor: Creditor
      /// Fps or BookTransfer, the schemes Griffin sends over.
      Scheme: PaymentScheme
      FirstDate: DateTime
      Frequency: OrderFrequency
      End: OrderEnd
      NonWorkingDays: NonWorkingDayRule }

/// One payment an order calls for.
type OrderOccurrence =
    { /// 1 for the first.
      Number: int
      /// The date the schedule names. Store this one as the last run: Due can move when a calendar changes.
      ScheduledDate: DateTime
      /// ScheduledDate after the non-working day rule.
      Due: DateTime
      /// The order's reference and ScheduledDate: the payment's identity at Griffin.
      Reference: string }

type OccurrenceOutcome =
    /// This call submitted the payment. Follow the submission to delivery.
    | Submitted of Payment * Submission
    /// An earlier run submitted this payment, so this one did not. The submission can show Failed or Returned: the
    /// occurrence is not paid again by itself, because its reference now names that payment.
    | AlreadySubmitted of Payment * Submission
    /// The payment was rejected, so it will not go.
    | Rejected of Payment
    /// Griffin may or may not hold the payment: creating it failed without a clear answer. Run this occurrence again
    /// later, not at once: the next run looks for the payment first, and can only find it once Griffin has finished.
    | CreationUnknown of GriffinFailure

/// Works out when a standing order pays and pays one occurrence. It holds no state: the caller stores each order and its
/// last completed ScheduledDate, and runs one order at a time, because two runs at once can each find no payment and
/// each create one.
module StandingOrder =

    /// Griffin's 35-character reference, less the space and date each occurrence adds.
    [<Literal>]
    let ReferenceLimit = 26

    /// Griffin refuses a larger Faster Payment (payment-amount-exceeds-scheme-limit).
    let fasterPaymentsLimit = 1000000m

    let private validateFrequency frequency =
        match frequency with
        | OrderFrequency.Once -> Ok()
        | OrderFrequency.EveryWeeks weeks when weeks < 1 -> Error "The week interval is below 1"
        | OrderFrequency.EveryMonths(months, _) when months < 1 -> Error "The month interval is below 1"
        | OrderFrequency.EveryMonths(_, MonthDay.Day day) when day < 1 || day > 31 -> Error $"Day {day} is not 1 to 31"
        | OrderFrequency.EveryWeeks _
        | OrderFrequency.EveryMonths _ -> Ok()

    /// The order, or why it cannot run.
    let validate (order: StandingOrder) : Result<StandingOrder, string> =
        let creditor =
            match order.Creditor with
            | ToUkAccount account -> UkAccount.validate account |> Result.map ignore
            | ToPayee _
            | ToGriffinAccount _ -> Ok()

        let ending =
            match order.End with
            | OrderEnd.Count count when count < 1 -> Error "The occurrence count is below 1"
            | OrderEnd.LastDate last when last.Date < order.FirstDate.Date -> Error "The last date is before the first date"
            | OrderEnd.NoEnd
            | OrderEnd.LastDate _
            | OrderEnd.Count _ -> Ok()

        if String.IsNullOrWhiteSpace order.Reference then
            Error "The reference is empty"
        elif order.Reference <> order.Reference.Trim() then
            Error "The reference starts or ends with a space"
        elif order.Reference.Length > ReferenceLimit then
            Error $"The reference is over {ReferenceLimit} characters"
        elif order.Amount.Currency <> "GBP" then
            Error $"The amount is in {order.Amount.Currency}, and Griffin accounts hold GBP"
        elif order.Amount.Value <= 0m then
            Error "The amount is not positive"
        elif Math.Round(order.Amount.Value, 2) <> order.Amount.Value then
            Error "The amount has fractions of a penny"
        elif order.Scheme <> PaymentScheme.Fps && order.Scheme <> PaymentScheme.BookTransfer then
            Error $"Griffin does not send over {order.Scheme.Value}"
        elif order.Scheme = PaymentScheme.Fps && order.Amount.Value > fasterPaymentsLimit then
            Error "The amount is over the Faster Payments limit"
        else
            creditor
            |> Result.bind (fun () -> ending)
            |> Result.bind (fun () -> validateFrequency order.Frequency)
            |> Result.map (fun () -> order)

    let private inMonth (monthStart: DateTime) (day: MonthDay) =
        let daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month)

        match day with
        | MonthDay.Last -> monthStart.AddDays(float (daysInMonth - 1))
        | MonthDay.Day chosen -> monthStart.AddDays(float (min chosen daysInMonth - 1))

    /// Each date is worked out from the first, so a short month does not move the dates after it.
    let private scheduledDates (order: StandingOrder) =
        let first = order.FirstDate.Date

        match order.Frequency with
        | OrderFrequency.Once -> Seq.singleton first
        | OrderFrequency.EveryWeeks weeks -> Seq.initInfinite (fun step -> first.AddDays(float (7 * weeks * step)))
        | OrderFrequency.EveryMonths(months, day) ->
            let monthStart = DateTime(first.Year, first.Month, 1)

            Seq.initInfinite (fun step -> inMonth (monthStart.AddMonths(months * step)) day)
            |> Seq.filter (fun date -> date >= first)

    let private dueDate (calendar: WorkingCalendar) rule (date: DateTime) =
        let moved (step: float) =
            let found =
                Seq.init 32 (fun days -> date.AddDays(step * float days))
                |> Seq.tryFind calendar.IsWorkingDay

            match found with
            | Some day -> day
            | None ->
                let named = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                invalidArg (nameof calendar) $"The calendar has no working day within a month of {named}"

        match rule with
        | NonWorkingDayRule.PayOnTheDay -> date
        | NonWorkingDayRule.NextWorkingDay -> moved 1.0
        | NonWorkingDayRule.PreviousWorkingDay -> moved -1.0

    /// Every occurrence of the order in date order. Endless for OrderEnd.NoEnd, so take from it as needed. A frequency
    /// that never moves on (validate says which) is an ArgumentException here, not a loop that doesn't end.
    let occurrences (calendar: WorkingCalendar) (order: StandingOrder) : seq<OrderOccurrence> =
        match validateFrequency order.Frequency with
        | Error reason -> invalidArg (nameof order) reason
        | Ok() -> ()

        let dates =
            match order.End with
            | OrderEnd.NoEnd -> scheduledDates order
            | OrderEnd.Count count -> scheduledDates order |> Seq.truncate count
            | OrderEnd.LastDate last -> scheduledDates order |> Seq.takeWhile (fun date -> date <= last.Date)

        dates
        |> Seq.mapi (fun index date ->
            { Number = index + 1
              ScheduledDate = date
              Due = dueDate calendar order.NonWorkingDays date
              Reference = order.Reference + " " + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) })

    let private ofOption value =
        match value with
        | Some found -> ValueSome found
        | None -> ValueNone

    /// The first occurrence due after the date, if the order has one left.
    let nextDue (calendar: WorkingCalendar) (after: DateTime) (order: StandingOrder) =
        occurrences calendar order
        |> Seq.tryFind (fun occurrence -> occurrence.Due > after.Date)
        |> ofOption

    /// The occurrences to pay today, oldest first: those due by today and scheduled after lastCompleted, the
    /// ScheduledDate of the last occurrence the caller finished. After a missed run this includes every occurrence
    /// missed, so check their Due dates if an old payment should not go out late.
    let dueOccurrences (calendar: WorkingCalendar) (lastCompleted: DateTime voption) (today: DateTime) (order: StandingOrder) =
        occurrences calendar order
        |> Seq.takeWhile (fun occurrence -> occurrence.Due <= today.Date)
        |> Seq.filter (fun occurrence ->
            lastCompleted |> ValueOption.forall (fun completed -> occurrence.ScheduledDate > completed.Date))
        |> List.ofSeq

    /// Before the occurrence could first have been paid: its due date can move by up to 32 days if the rule or calendar has
    /// changed since, and a day covers the difference between UK dates and UTC. The reference holds the date, so looking
    /// further back can't find another occurrence.
    let private createdSince (occurrence: OrderOccurrence) =
        let earliest = (min occurrence.Due occurrence.ScheduledDate).AddDays -33.0
        earliest.ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture)

    /// Which payment with the occurrence's reference is the occurrence's. A submitted one wins, so a second copy is never
    /// sent. It fails rather than guess: another amount means the reference is another payment's, and two unsent copies could
    /// each be the one sent next.
    let private occurrencePayment (order: StandingOrder) (occurrence: OrderOccurrence) (matching: Payment list) =
        let sendable = matching |> List.filter (fun payment -> payment.RejectedAt.IsNone)
        let submitted = sendable |> List.filter (fun payment -> payment.LatestSubmissionUrl.IsSome)

        match matching |> List.tryFind (fun payment -> payment.Amount <> order.Amount), submitted, sendable, matching with
        | Some other, _, _, _ ->
            Error(InvalidResponse $"Payment {other.Url} has the reference {occurrence.Reference} and another amount")
        | None, first :: _, _, _ -> Ok(ValueSome first)
        | None, [], [ only ], _ -> Ok(ValueSome only)
        | None, [], [], rejected :: _ -> Ok(ValueSome rejected)
        | None, [], [], [] -> Ok ValueNone
        | None, [], several, _ ->
            Error(InvalidResponse $"{several.Length} unsent payments have the reference {occurrence.Reference}")

    /// The account's payment for this occurrence. A page that cannot be read, in whole or in part, could hold that payment,
    /// so it fails the lookup rather than risk creating a second one.
    let private findPaymentAsync
        (client: GriffinClient)
        (paymentsUrl: string)
        (order: StandingOrder)
        (occurrence: OrderOccurrence)
        (ct: CancellationToken)
        =
        task {
            let link =
                Api.withQuery
                    paymentsUrl
                    (Api.Query.atLeast "created-at" (createdSince occurrence)
                     @ Api.Query.paymentDirection PaymentDirection.Outbound
                     @ Api.Query.pageSize 200)

            match! Api.allPagesAsync (Api.listPaymentsAsync client) link ct with
            | Error failure -> return Error failure
            | Ok page when not page.Unparsed.IsEmpty ->
                return Error(InvalidResponse $"{page.Unparsed.Length} payments could not be read, so {occurrence.Reference} may exist")
            | Ok page ->
                return
                    page.Items
                    |> List.filter (fun payment ->
                        payment.Reference
                        |> ValueOption.exists (fun reference ->
                            String.Equals(reference.Trim(), occurrence.Reference.Trim(), StringComparison.OrdinalIgnoreCase)))
                    |> occurrencePayment order occurrence
        }

    let private submissionOfAsync client (payment: Payment) ct =
        task {
            match payment.LatestSubmissionUrl with
            | ValueSome url ->
                let! submission = Api.getSubmissionAsync client url ct
                return submission |> Result.map (fun read -> AlreadySubmitted(payment, read))
            | ValueNone ->
                match payment.SubmissionsUrl with
                | ValueNone -> return Error(InvalidResponse $"Payment {payment.Url} has no submissions link")
                | ValueSome url ->
                    let! submissions = Api.listSubmissionsAsync client url ct

                    return
                        submissions
                        |> Result.bind (fun page ->
                            match page.Items with
                            | latest :: _ -> Ok(AlreadySubmitted(payment, latest))
                            | [] -> Error(InvalidResponse $"Payment {payment.Url} answered 410 and has no submission"))
        }

    /// Submits a payment that has no submission yet. Griffin's submit is idempotent: a second one answers 410.
    let private submitAsync client (scheme: PaymentScheme) (payment: Payment) ct =
        task {
            match payment.SubmissionsUrl with
            | ValueNone -> return Error(InvalidResponse $"Payment {payment.Url} has no submissions link")
            | ValueSome url ->
                match! Api.submitPaymentAsync client url scheme ct with
                | Ok submission -> return Ok(Submitted(payment, submission))
                | Error(ApiError(410, _)) ->
                    match! Api.getPaymentAsync client payment.Url ct with
                    | Error failure -> return Error failure
                    | Ok current when current.RejectedAt.IsSome -> return Ok(Rejected current)
                    | Ok current -> return! submissionOfAsync client current ct
                | Error failure -> return Error failure
        }

    /// An error that leaves open whether Griffin created the payment.
    let private creationUnclear (failure: GriffinFailure) =
        match failure with
        | ApiError(status, _) -> status >= 500
        | TransportError _
        | InvalidResponse _ -> true
        | InvalidRequest _
        | SigningFailed _
        | ForeignLink _
        | WebhookRejected _ -> false

    /// Pays one occurrence from the account (BankAccount.PaymentsUrl), safe to run again after any failure. Griffin takes
    /// no idempotency key when creating a payment, so this looks for the occurrence's Reference first and creates only if
    /// it finds none, then submits, which is idempotent at Griffin. A retry after a failed submit pays once.
    ///
    /// The one way to pay twice is a second run before Griffin lists a payment whose creation had no clear answer: see
    /// CreationUnknown. A refused payment is Error(ApiError(422, _)) (PaymentRefusal.ofFailure), and nothing is lost by
    /// running it again once the cause is fixed.
    let payAsync
        (client: GriffinClient)
        (paymentsUrl: string)
        (order: StandingOrder)
        (occurrence: OrderOccurrence)
        (ct: CancellationToken)
        : Task<Result<OccurrenceOutcome, GriffinFailure>> =
        task {
            match validate order with
            | Error reason -> return Error(InvalidRequest reason)
            | Ok order ->
                match! findPaymentAsync client paymentsUrl order occurrence ct with
                | Error failure -> return Error failure
                | Ok(ValueSome existing) when existing.RejectedAt.IsSome -> return Ok(Rejected existing)
                | Ok(ValueSome existing) when existing.LatestSubmissionUrl.IsSome -> return! submissionOfAsync client existing ct
                | Ok(ValueSome existing) -> return! submitAsync client order.Scheme existing ct
                | Ok ValueNone ->
                    match! Api.createPaymentAsync client paymentsUrl order.Creditor order.Amount (ValueSome occurrence.Reference) ct with
                    | Ok payment -> return! submitAsync client order.Scheme payment ct
                    | Error failure when creationUnclear failure -> return Ok(CreationUnknown failure)
                    | Error failure -> return Error failure
        }
