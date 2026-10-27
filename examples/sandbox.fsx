// Onboards a made-up individual in the Griffin sandbox, opens an embedded account for them, moves money into
// it from the organization's primary account by book transfer, and lists the account's transactions.
// Every run creates a new legal person and account in the sandbox organization.
//
//   dotnet build -c Release
//   $env:GRIFFIN_API_KEY = "g-test-..."
//   dotnet fsi examples/sandbox.fsx

#r "../src/GriffinClient/bin/Release/net8.0/GriffinClient.dll"

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Griffin

let apiKey =
    match Environment.GetEnvironmentVariable "GRIFFIN_API_KEY" with
    | null
    | "" -> failwith "Set GRIFFIN_API_KEY to a sandbox key"
    | key when not (key.StartsWith "g-test-") -> failwith "This example only runs against a sandbox (g-test-) key"
    | key -> key

let client =
    { Config = GriffinConfig.create apiKey
      Http = new HttpClient() }

let ct = CancellationToken.None

let orFail label (result: Task<Result<'T, GriffinFailure>>) =
    result.Result |> Result.defaultWith (fun e -> failwith $"{label}: %A{e}")

/// Polls read until done holds, at most attempts times, delay apart.
[<TailCall>]
let rec pollUntil label (attempts: int) (delay: TimeSpan) (read: unit -> 'T) (isDone: 'T -> bool) =
    let value = read ()

    if isDone value then value
    elif attempts <= 1 then failwith $"{label}: not done after polling"
    else
        Thread.Sleep delay
        pollUntil label (attempts - 1) delay read isDone

let org = Api.getOwnOrganizationAsync client ct |> orFail "organization"
printfn $"Organization: {org.DisplayName} ({org.Mode})"

let products = Api.listBankProductsAsync client org.BankProductsUrl ct |> orFail "products"

let embedded =
    products.Items
    |> List.find (fun p -> p.ProductClass = "embedded" && p.OwnerType = ValueSome LegalPersonType.Individual)

let workflows = Api.listWorkflowsAsync client org.WorkflowsUrl ct |> orFail "workflows"

let workflow =
    workflows.Items
    |> List.find (fun w ->
        w.LegalPersonType = LegalPersonType.Individual
        && w.DisplayName.StartsWith("Individual", StringComparison.Ordinal)
        && not w.Disabled)

printfn $"Product: %s{embedded.DisplayName}; workflow: %s{workflow.DisplayName} needs %A{workflow.RequiredClaimTypes}"

let claims =
    [ Claims.individualIdentity "Ada" ValueNone "Example" (DateTime(1985, 6, 15))
      Claims.contactDetails "ada.example@example.com"
      Claims.mobileNumber "+447700900123"
      Claims.ukResidence (ValueSome "10") ValueNone "Downing Street" "London" "SW1A 2AA" "GB"
      Claims.nationality "GB"
      Claims.taxResidencies [ "GB" ]
      Claims.usCitizen false
      Claims.employment (Employed("information-technology", ValueSome "software-engineer"))
      Claims.individualIncome (Money.gbp 45000m)
      Claims.initialDeposit (Money.gbp 100m)
      Claims.purposesOfAccount [ "everyday-spending"; "savings" ]
      Claims.sourcesOfFunds [ "salary-or-bonus" ]
      Claims.cashPayments false
      Claims.internationalPaymentsCountries [] ]

let person =
    Api.createLegalPersonAsync client org.LegalPersonsUrl "Ada Example" LegalPersonType.Individual claims ct
    |> orFail "legal person"

printfn $"Legal person: {person.Url}"

let verification =
    Api.createVerificationAsync client person.VerificationsUrl.Value workflow.Url ct
    |> orFail "verification"

let checkedVerification =
    pollUntil "verification" 30 (TimeSpan.FromSeconds 2.0)
        (fun () -> Api.getVerificationAsync client verification.Url ct |> orFail "verification")
        (fun v -> v.Status <> VerificationStatus.Pending && v.Status <> VerificationStatus.InProgress)

printfn $"Verification: %s{checkedVerification.Status.Value}, risk %A{checkedVerification.RiskRating}"

let decided =
    pollUntil "decision" 30 (TimeSpan.FromSeconds 2.0)
        (fun () -> Api.getLegalPersonAsync client person.Url ct |> orFail "legal person")
        (fun p -> p.LatestDecision.IsSome)

let decision = decided.LatestDecision.Value
printfn $"Decision: %s{decision.Outcome.Value} by %A{decision.DecisionMaker}"

for reason in decision.DeclineReasons do
    printfn $"  declined: %s{reason.Code} %s{reason.Title} %A{reason.ClaimTypes}"

if decision.Outcome <> DecisionOutcome.Accepted then
    failwith "Not accepted, stopping"

let opened =
    Api.openBankAccountAsync client embedded.AccountsUrl "Ada's account" person.Url (ValueSome person.Url) ct
    |> orFail "open account"

let account =
    pollUntil "account" 30 (TimeSpan.FromSeconds 2.0)
        (fun () -> Api.getBankAccountAsync client opened.Url ct |> orFail "account")
        (fun a -> a.Status = AccountStatus.Open)

let address = account.BankAddresses |> List.head
printfn $"Account open: %s{address.BankId} %s{address.AccountNumber}, balance %A{account.Balance}"

let primary =
    Api.allPagesAsync (Api.listBankAccountsAsync client) org.BankAccountsUrl ct
    |> orFail "accounts"
    |> _.Items
    |> List.find (fun a -> a.ProductType = ValueSome BankProductType.OperationalAccount && a.Status = AccountStatus.Open)

let payment =
    Api.createPaymentAsync client primary.PaymentsUrl.Value (ToGriffinAccount account.Url) (Money.gbp 25.50m) (ValueSome "GriffinClient sample")
        ct
    |> orFail "payment"

let submitted =
    Api.submitPaymentAsync client payment.SubmissionsUrl.Value PaymentScheme.BookTransfer ct
    |> orFail "submit"

let delivered =
    pollUntil "submission" 30 (TimeSpan.FromSeconds 2.0)
        (fun () -> Api.getSubmissionAsync client submitted.Url ct |> orFail "submission")
        (fun s -> s.Status <> SubmissionStatus.Processing && s.Status <> SubmissionStatus.Scheduled)

printfn $"Submission: %s{delivered.Status.Value} via %A{delivered.Scheme}"

let transactions =
    Api.allPagesAsync (Api.listTransactionsAsync client) account.TransactionsUrl.Value ct
    |> orFail "transactions"

for t in transactions.Items do
    let posted =
        t.PostedAt |> ValueOption.map _.ToString("yyyy-MM-dd HH:mm") |> ValueOption.defaultValue ""

    let reference = t.Reference |> ValueOption.defaultValue ""
    let origin = t.Origin |> ValueOption.map _.Value |> ValueOption.defaultValue ""
    printfn $"  {posted} {t.Direction.Value} {t.BalanceChange.Value} {t.BalanceChange.Currency} ({origin}) {reference} -> {t.AccountBalance.Value}"

if not transactions.Unparsed.IsEmpty then
    printfn $"  ({transactions.Unparsed.Length} transactions did not parse)"

let events =
    Api.listEventsAsync client (Api.withQuery org.EventsUrl.Value [ ("page[size]", "10") ]) ct
    |> orFail "events"

printfn "Latest events:"

for e in events.Items do
    let kind =
        match e.Payload with
        | Other(t, _) -> $"Other {t}"
        | Unavailable v -> $"Unavailable, needs {v}"
        | p -> ($"%A{p}").Split([| ' '; '\n' |]).[0]

    printfn $"  {e.EventType}: {kind}"
