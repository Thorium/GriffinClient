namespace Griffin

open System
open System.Globalization
open System.Net.Http
open System.Threading
open System.Threading.Tasks

/// Griffin's wire strings for the closed sets below: one table per type, read in both directions.
module internal Wire =

    let name (names: ('T * string) list) (other: 'T -> string) (value: 'T) =
        match names |> List.tryFind (fun (v, _) -> v = value) with
        | Some(_, n) -> n
        | None -> other value

    let parse (names: ('T * string) list) (other: string -> 'T) (text: string) =
        match names |> List.tryFind (fun (_, n) -> n = text) with
        | Some(v, _) -> v
        | None -> other text

/// Result chaining for the verification and validation steps.
type internal ResultBuilder() =
    member _.Bind(r: Result<'T, 'E>, f: 'T -> Result<'U, 'E>) = Result.bind f r
    member _.Return(v: 'T) : Result<'T, 'E> = Ok v
    member _.ReturnFrom(r: Result<'T, 'E>) = r

module internal ResultBuilder =
    let result = ResultBuilder()

/// Signs Griffin message signatures (RFC 9421, Ed25519). Live organizations must sign every request.
/// Sign gets the signature base as UTF-8 bytes and returns the raw 64-byte Ed25519 signature. Back it with
/// a key store or KMS where you can, so the private key stays out of the process.
type RequestSigner =
    { /// Key id Griffin assigned when the public key was registered (Settings > Message signatures).
      KeyId: string
      Sign: byte[] -> CancellationToken -> Task<byte[]> }

    /// For C#: a signer from a delegate.
    static member Create(keyId: string, sign: Func<byte[], CancellationToken, Task<byte[]>>) =
        { KeyId = keyId
          Sign = fun data ct -> sign.Invoke(data, ct) }

/// API key, version and signing settings. Printing it (ToString, %A) hides the API key.
[<StructuredFormatDisplay("{Display}")>]
type GriffinConfig =
    { ApiKey: string
      /// Every link is resolved against this; links to other hosts are refused, so the key never leaves it.
      BaseUrl: string
      /// Sent as Griffin-Version.
      ApiVersion: string
      /// Signs every request when set; required in live mode.
      Signer: RequestSigner voption
      /// expires - created of a request signature: more than zero and at most 300 seconds, Griffin's maximum.
      SignatureLifetime: TimeSpan }

    member this.Display =
        $"GriffinConfig {{ BaseUrl = %s{this.BaseUrl}; ApiVersion = %s{this.ApiVersion}; ApiKey = ***; Signed = {this.Signer.IsSome} }}"

    override this.ToString() = this.Display

module GriffinConfig =

    /// The API version this client parses.
    [<Literal>]
    let ApiVersion = "2026-07-09"

    /// Griffin's longest accepted signature lifetime.
    let MaxSignatureLifetime = TimeSpan.FromSeconds 300.0

    /// api.griffin.com, the version this client parses, no request signing, 300-second signatures.
    let create apiKey =
        { ApiKey = apiKey
          BaseUrl = "https://api.griffin.com"
          ApiVersion = ApiVersion
          Signer = ValueNone
          SignatureLifetime = MaxSignatureLifetime }

type GriffinConfig with

    /// For C#: GriffinConfig.create.
    static member Create(apiKey: string) = GriffinConfig.create apiKey

/// Configuration plus the HttpClient used for every call to Griffin. The caller owns the HttpClient.
type GriffinClient = { Config: GriffinConfig; Http: HttpClient }

/// An amount; Griffin sends the value as a decimal string.
type Money = { Currency: string; Value: decimal }

module Money =

    let gbp (value: decimal) = { Currency = "GBP"; Value = value }

    /// Griffin's JSON form, the value with at least two decimals: {"currency": "GBP", "value": "20.00"}.
    let toJson (money: Money) : obj =
        box
            {| currency = money.Currency
               value = money.Value.ToString("0.00##########################", CultureInfo.InvariantCulture) |}

type Money with

    /// For C#: Money.gbp.
    static member Gbp(value: decimal) = Money.gbp value

/// A UK account: holder name, 6-digit sort code (Griffin's bank-id) and 8-digit account number.
type UkAccount =
    { AccountHolder: string
      SortCode: string
      AccountNumber: string }

module UkAccount =

    let private digits count (text: string) =
        text.Length = count && text |> Seq.forall (fun c -> c >= '0' && c <= '9')

    /// The account, or why Griffin would refuse it: sort code and account number are ASCII digits only.
    let validate (account: UkAccount) : Result<UkAccount, string> =
        if String.IsNullOrWhiteSpace account.AccountHolder then
            Error "Account holder is empty"
        elif not (digits 6 account.SortCode) then
            Error $"Sort code '{account.SortCode}' is not 6 digits"
        elif not (digits 8 account.AccountNumber) then
            Error $"Account number '{account.AccountNumber}' is not 8 digits"
        else
            Ok account

/// Why a webhook delivery was not accepted.
type WebhookRejection =
    | MissingHeader of name: string
    | MalformedSignature of reason: string
    | NoSignatures
    | UnknownKey of keyId: string
    | UnsupportedKey of keyId: string * algorithm: string * curve: string
    | InvalidKey of keyId: string * reason: string
    /// The signature does not cover content-digest, so it would not bind the body.
    | DigestNotCovered
    | NoExpiry
    | Expired of expiredAt: DateTimeOffset
    /// The signature covers a component the delivery lacks.
    | MissingComponent of name: string
    | SignatureMismatch
    | DigestMismatch

/// One entry of the "errors" array Griffin returns with a non-success status.
type GriffinError =
    { Title: string
      Detail: string voption
      /// Machine-readable code where Griffin sends one, e.g. "unknown-signing-key-id".
      Code: string voption
      /// The request field (e.g. "claims/7/occupation") or header (e.g. "signature-input/label") the error refers
      /// to, where Griffin sends one.
      SourcePointer: string voption
      /// Documentation link for the error.
      About: string voption }

type GriffinFailure =
    | ApiError of httpStatus: int * errors: GriffinError list
    | TransportError of exn
    | InvalidResponse of message: string
    /// The request was not sent: an argument or the configuration is invalid.
    | InvalidRequest of reason: string
    /// The request signer threw.
    | SigningFailed of exn
    /// A link pointed outside GriffinConfig.BaseUrl, so it was not followed.
    | ForeignLink of link: string
    | WebhookRejected of WebhookRejection

module GriffinFailure =

    let private errorText (e: GriffinError) =
        [ ValueSome e.Title; e.Detail; e.Code |> ValueOption.map (sprintf "(%s)") ]
        |> List.choose (function ValueSome s when s <> "" -> Some s | _ -> None)
        |> String.concat " "

    /// One line saying what went wrong, for logs and exception messages.
    let describe (failure: GriffinFailure) =
        match failure with
        | ApiError(status, []) -> $"Griffin answered HTTP {status}"
        | ApiError(status, errors) -> $"Griffin answered HTTP {status}: " + (errors |> List.map errorText |> String.concat "; ")
        | TransportError e -> $"Request failed: {e.Message}"
        | InvalidResponse message -> $"Unexpected response: {message}"
        | InvalidRequest reason -> $"Not sent: {reason}"
        | SigningFailed e -> $"Signing failed: {e.Message}"
        | ForeignLink link -> $"Not followed: {link} is outside BaseUrl"
        | WebhookRejected rejection -> $"Webhook rejected: %A{rejection}"

/// A list item that did not parse, kept so one unexpected item does not fail the whole list.
type UnparsedItem = { Reason: string; Raw: string }

/// One page of a list. Pass Next (or Prev) to the same list function for the adjacent page.
type Page<'T> =
    { Items: 'T list
      /// Items Griffin returned that this client could not read.
      Unparsed: UnparsedItem list
      Next: string voption
      Prev: string voption }

[<RequireQualifiedAccess>]
type OrganizationMode =
    | TestMode
    | LiveMode
    | Other of string

    static member private Names = [ TestMode, "test-mode"; LiveMode, "live-mode" ]
    member this.Value = Wire.name OrganizationMode.Names (function Other s -> s | TestMode | LiveMode -> "") this
    static member Parse text = Wire.parse OrganizationMode.Names Other text

[<RequireQualifiedAccess>]
type LegalPersonType =
    | Individual
    | Corporation
    | Other of string

    static member private Names = [ Individual, "individual"; Corporation, "corporation" ]
    member this.Value = Wire.name LegalPersonType.Names (function Other s -> s | Individual | Corporation -> "") this
    static member Parse text = Wire.parse LegalPersonType.Names Other text

[<RequireQualifiedAccess>]
type LegalPersonStatus =
    | Onboarding
    | Onboarded
    | OnboardingDeclined
    | Verified
    | Restricted
    | Other of string

    static member private Names =
        [ Onboarding, "onboarding"
          Onboarded, "onboarded"
          OnboardingDeclined, "onboarding-declined"
          Verified, "verified"
          Restricted, "restricted" ]

    member this.Value = Wire.name LegalPersonStatus.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse LegalPersonStatus.Names Other text

[<RequireQualifiedAccess>]
type ApplicationStatus =
    | Submitted
    | Referred
    | Accepted
    | Declined
    | Errored
    | Other of string

    static member private Names =
        [ Submitted, "submitted"
          Referred, "referred"
          Accepted, "accepted"
          Declined, "declined"
          Errored, "errored" ]

    member this.Value = Wire.name ApplicationStatus.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse ApplicationStatus.Names Other text

[<RequireQualifiedAccess>]
type DecisionOutcome =
    | Accepted
    | Declined
    | Other of string

    static member private Names = [ Accepted, "accepted"; Declined, "declined" ]
    member this.Value = Wire.name DecisionOutcome.Names (function Other s -> s | Accepted | Declined -> "") this
    static member Parse text = Wire.parse DecisionOutcome.Names Other text

[<RequireQualifiedAccess>]
type DecisionMaker =
    | System
    | OpsUser
    | User
    | Other of string

    static member private Names = [ System, "system"; OpsUser, "ops-user"; User, "user" ]
    member this.Value = Wire.name DecisionMaker.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse DecisionMaker.Names Other text

[<RequireQualifiedAccess>]
type VerificationStatus =
    | Pending
    | InProgress
    | ChecksComplete
    | ChecksDeclined
    | Failed
    | Other of string

    static member private Names =
        [ Pending, "pending"
          InProgress, "in-progress"
          ChecksComplete, "checks-complete"
          ChecksDeclined, "checks-declined"
          Failed, "failed" ]

    member this.Value = Wire.name VerificationStatus.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse VerificationStatus.Names Other text

[<RequireQualifiedAccess>]
type RiskRating =
    | InProgress
    | Low
    | Medium
    | High
    | Prohibited
    | Other of string

    static member private Names =
        [ InProgress, "in-progress"
          Low, "low-risk"
          Medium, "medium-risk"
          High, "high-risk"
          Prohibited, "prohibited-risk" ]

    member this.Value = Wire.name RiskRating.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse RiskRating.Names Other text

[<RequireQualifiedAccess>]
type IdvCheckStatus =
    | Pending
    | Complete
    | Errored
    | Expired
    | Other of string

    static member private Names =
        [ Pending, "pending"; Complete, "complete"; Errored, "errored"; Expired, "expired" ]

    member this.Value = Wire.name IdvCheckStatus.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse IdvCheckStatus.Names Other text

[<RequireQualifiedAccess>]
type FundingType =
    | Dedicated
    | Pooled
    | Other of string

    static member private Names = [ Dedicated, "dedicated"; Pooled, "pooled" ]
    member this.Value = Wire.name FundingType.Names (function Other s -> s | Dedicated | Pooled -> "") this
    static member Parse text = Wire.parse FundingType.Names Other text

[<RequireQualifiedAccess>]
type BankProductType =
    | EmbeddedAccount
    | SavingsAccount
    | OperationalAccount
    | ClientMoneyAccount
    | SafeguardingAccount
    | Other of string

    static member private Names =
        [ EmbeddedAccount, "embedded-account"
          SavingsAccount, "savings-account"
          OperationalAccount, "operational-account"
          ClientMoneyAccount, "client-money-account"
          SafeguardingAccount, "safeguarding-account" ]

    member this.Value = Wire.name BankProductType.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse BankProductType.Names Other text

/// An account is usable once Open.
[<RequireQualifiedAccess>]
type AccountStatus =
    | Opening
    | Open
    | Closing
    | Closed
    | Other of string

    static member private Names = [ Opening, "opening"; Open, "open"; Closing, "closing"; Closed, "closed" ]
    member this.Value = Wire.name AccountStatus.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse AccountStatus.Names Other text

[<RequireQualifiedAccess>]
type RestrictionType =
    /// Blocks outbound payments; inbound payments still arrive.
    | Freeze
    /// Blocks inbound and outbound payments.
    | Block
    | Other of string

    static member private Names = [ Freeze, "freeze"; Block, "block" ]
    member this.Value = Wire.name RestrictionType.Names (function Other s -> s | Freeze | Block -> "") this
    static member Parse text = Wire.parse RestrictionType.Names Other text

/// Only an Active restriction can be updated or lifted.
[<RequireQualifiedAccess>]
type RestrictionStatus =
    | Active
    | Lifted
    | Other of string

    static member private Names = [ Active, "active"; Lifted, "lifted" ]
    member this.Value = Wire.name RestrictionStatus.Names (function Other s -> s | Active | Lifted -> "") this
    static member Parse text = Wire.parse RestrictionStatus.Names Other text

[<RequireQualifiedAccess>]
type RestrictionReason =
    | CustomerRequest
    | Dormant
    | Deceased
    | RiskAppetite
    | Sanctions
    | LawEnforcement
    | TerroristFinancing
    | MoneyLaundering
    | FraudAppVictim
    | FraudAppPerpetrator
    | FraudFirstParty
    | FraudThirdParty
    /// Griffin's "other" reason.
    | OtherReason
    | Other of string

    static member private Names =
        [ CustomerRequest, "customer-request"
          Dormant, "dormant"
          Deceased, "deceased"
          RiskAppetite, "risk-appetite"
          Sanctions, "sanctions"
          LawEnforcement, "law-enforcement"
          TerroristFinancing, "terrorist-financing"
          MoneyLaundering, "money-laundering"
          FraudAppVictim, "fraud-app-victim"
          FraudAppPerpetrator, "fraud-app-perpetrator"
          FraudFirstParty, "fraud-1st-party"
          FraudThirdParty, "fraud-3rd-party"
          OtherReason, "other" ]

    member this.Value = Wire.name RestrictionReason.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse RestrictionReason.Names Other text

[<RequireQualifiedAccess>]
type RestrictionLegalStatus =
    | NotApplicable
    | Suspected
    | FormalChargeFiled
    | ConvictionConfirmed
    | Other of string

    static member private Names =
        [ NotApplicable, "not-applicable"
          Suspected, "suspected"
          FormalChargeFiled, "formal-charge-filed"
          ConvictionConfirmed, "conviction-confirmed" ]

    member this.Value = Wire.name RestrictionLegalStatus.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse RestrictionLegalStatus.Names Other text

[<RequireQualifiedAccess>]
type StatementFormat =
    | GriffinPdf
    | XeroCsv
    | Other of string

    static member private Names = [ GriffinPdf, "griffin-pdf"; XeroCsv, "xero-csv" ]
    member this.Value = Wire.name StatementFormat.Names (function Other s -> s | GriffinPdf | XeroCsv -> "") this
    static member Parse text = Wire.parse StatementFormat.Names Other text

[<RequireQualifiedAccess>]
type StatementStatus =
    | Pending
    | Generated
    | Failed
    | Other of string

    static member private Names = [ Pending, "pending"; Generated, "generated"; Failed, "failed" ]
    member this.Value = Wire.name StatementStatus.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse StatementStatus.Names Other text

[<RequireQualifiedAccess>]
type MandateStatus =
    | Active
    | Cancelled
    | Expired
    | Other of string

    static member private Names = [ Active, "active"; Cancelled, "cancelled"; Expired, "expired" ]
    member this.Value = Wire.name MandateStatus.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse MandateStatus.Names Other text

[<RequireQualifiedAccess>]
type MandateCancellationReason =
    | CancelledByDebtor
    | CancelledByCreditor
    | CancelledByGriffin
    | AccountClosed
    | DebtorDeceased
    | Other of string

    static member private Names =
        [ CancelledByDebtor, "cancelled-by-debtor"
          CancelledByCreditor, "cancelled-by-creditor"
          CancelledByGriffin, "cancelled-by-griffin"
          AccountClosed, "account-closed"
          DebtorDeceased, "debtor-deceased" ]

    member this.Value = Wire.name MandateCancellationReason.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse MandateCancellationReason.Names Other text

[<RequireQualifiedAccess>]
type CollectionFrequency =
    | Daily
    | Weekly
    | Fortnightly
    | Monthly
    | Bimonthly
    | Quarterly
    | Yearly
    | OnDemand
    | Other of string

    static member private Names =
        [ Daily, "daily"
          Weekly, "weekly"
          Fortnightly, "fortnightly"
          Monthly, "monthly"
          Bimonthly, "bimonthly"
          Quarterly, "quarterly"
          Yearly, "yearly"
          OnDemand, "on-demand" ]

    member this.Value = Wire.name CollectionFrequency.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse CollectionFrequency.Names Other text

/// What a Direct Debit may collect.
type MandateAmount =
    | Fixed of Money
    /// Up to maximum, when the mandate sets one.
    | Variable of maximum: Money voption

[<RequireQualifiedAccess>]
type PayeeStatus =
    | Active
    | Deactivated
    | Other of string

    static member private Names = [ Active, "active"; Deactivated, "deactivated" ]
    member this.Value = Wire.name PayeeStatus.Names (function Other s -> s | Active | Deactivated -> "") this
    static member Parse text = Wire.parse PayeeStatus.Names Other text

/// Whose account a Confirmation of Payee check expects.
[<RequireQualifiedAccess>]
type AccountClassification =
    | Personal
    | Business
    | Other of string

    static member private Names = [ Personal, "personal"; Business, "business" ]
    member this.Value = Wire.name AccountClassification.Names (function Other s -> s | Personal | Business -> "") this
    static member Parse text = Wire.parse AccountClassification.Names Other text

[<RequireQualifiedAccess>]
type CopStatus =
    | Verified
    | Unverified
    | Other of string

    static member private Names = [ Verified, "verified"; Unverified, "unverified" ]
    member this.Value = Wire.name CopStatus.Names (function Other s -> s | Verified | Unverified -> "") this
    static member Parse text = Wire.parse CopStatus.Names Other text

[<RequireQualifiedAccess>]
type CopResult =
    | Match
    | CloseMatch
    | NoMatch
    | Other of string

    static member private Names = [ Match, "match"; CloseMatch, "close-match"; NoMatch, "no-match" ]
    member this.Value = Wire.name CopResult.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse CopResult.Names Other text

/// Why a Confirmation of Payee check did not fully match.
[<RequireQualifiedAccess>]
type CopReason =
    | NoMatchName
    | CloseMatchName
    | NoMatchSecondaryRef
    | AccountNotSupported
    | AccountSwitched
    | MatchNameBusinessPersonal
    | MatchNamePersonalBusiness
    | CloseMatchNameBusinessPersonal
    | CloseMatchNamePersonalBusiness
    | Other of string

    static member private Names =
        [ NoMatchName, "no-match-name"
          CloseMatchName, "close-match-name"
          NoMatchSecondaryRef, "no-match-secondary-ref"
          AccountNotSupported, "account-not-supported"
          AccountSwitched, "account-switched"
          MatchNameBusinessPersonal, "match-name-business-personal"
          MatchNamePersonalBusiness, "match-name-personal-business"
          CloseMatchNameBusinessPersonal, "close-match-name-business-personal"
          CloseMatchNamePersonalBusiness, "close-match-name-personal-business" ]

    member this.Value = Wire.name CopReason.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse CopReason.Names Other text

/// The answer a sandbox Confirmation of Payee simulation gives (Api.simulateCopAsync).
[<RequireQualifiedAccess>]
type CopSimulation =
    | FullMatch
    | AccountNotFound
    /// A non-matching answer with this reason.
    | Reason of CopReason
    | Other of string

    member this.Value =
        match this with
        | FullMatch -> "full-match"
        | AccountNotFound -> "account-not-found"
        | Reason reason -> reason.Value
        | Other s -> s

/// How a payment moves. Payments are submitted by Fps or BookTransfer; Chaps and Bacs arrive only.
[<RequireQualifiedAccess>]
type PaymentScheme =
    | Fps
    | BookTransfer
    | Chaps
    | Bacs
    | Other of string

    static member private Names = [ Fps, "fps"; BookTransfer, "book-transfer"; Chaps, "chaps"; Bacs, "bacs" ]
    member this.Value = Wire.name PaymentScheme.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse PaymentScheme.Names Other text

[<RequireQualifiedAccess>]
type PaymentDirection =
    | Inbound
    | Outbound
    | Other of string

    static member private Names = [ Inbound, "inbound-payment"; Outbound, "outbound-payment" ]
    member this.Value = Wire.name PaymentDirection.Names (function Other s -> s | Inbound | Outbound -> "") this
    static member Parse text = Wire.parse PaymentDirection.Names Other text

[<RequireQualifiedAccess>]
type SubmissionStatus =
    | Processing
    | Scheduled
    | Delivered
    | Failed
    | Returned
    | Other of string

    static member private Names =
        [ Processing, "processing"
          Scheduled, "scheduled"
          Delivered, "delivered"
          Failed, "failed"
          Returned, "returned" ]

    member this.Value = Wire.name SubmissionStatus.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse SubmissionStatus.Names Other text

[<RequireQualifiedAccess>]
type AdmissionStatus =
    | Processing
    | Delivered
    | Returned
    | Cancelled
    | Other of string

    static member private Names =
        [ Processing, "processing"; Delivered, "delivered"; Returned, "returned"; Cancelled, "cancelled" ]

    member this.Value = Wire.name AdmissionStatus.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse AdmissionStatus.Names Other text

[<RequireQualifiedAccess>]
type BalanceDirection =
    | Credit
    | Debit
    | Other of string

    static member private Names = [ Credit, "credit"; Debit, "debit" ]
    member this.Value = Wire.name BalanceDirection.Names (function Other s -> s | Credit | Debit -> "") this
    static member Parse text = Wire.parse BalanceDirection.Names Other text

[<RequireQualifiedAccess>]
type TransactionOrigin =
    | Payment
    | Deposit
    | Transfer
    | Fee
    | Interest
    | CardTransaction
    | ManualPayment
    | Commission
    | Other of string

    static member private Names =
        [ Payment, "payment"
          Deposit, "deposit"
          Transfer, "transfer"
          Fee, "fee"
          Interest, "interest"
          CardTransaction, "card-transaction"
          ManualPayment, "manual-payment"
          Commission, "commission" ]

    member this.Value = Wire.name TransactionOrigin.Names (function Other s -> s | _ -> "") this
    static member Parse text = Wire.parse TransactionOrigin.Names Other text

[<RequireQualifiedAccess>]
type WebhookState =
    | Active
    | Inactive
    | Other of string

    static member private Names = [ Active, "active"; Inactive, "inactive" ]
    member this.Value = Wire.name WebhookState.Names (function Other s -> s | Active | Inactive -> "") this
    static member Parse text = Wire.parse WebhookState.Names Other text

type Index =
    { OrganizationUrl: string
      Raw: string }

type Organization =
    { /// The URL it was read from; the body itself has no self link.
      Url: string
      DisplayName: string
      Mode: OrganizationMode
      OwnLegalPersonUrl: string voption
      LegalPersonsUrl: string
      BankAccountsUrl: string
      BankProductsUrl: string
      WorkflowsUrl: string
      PaymentsUrl: string voption
      EventsUrl: string voption
      WebhooksUrl: string voption
      Raw: string }

    /// Where Api.searchLegalPersonsAsync posts.
    member this.LegalPersonsSearchUrl = this.Url + "/legal-persons/search"

    /// Where Api.searchBankAccountsAsync posts.
    member this.BankAccountsSearchUrl = this.Url + "/bank/accounts/search"

    /// Where Api.searchPaymentsAsync posts.
    member this.PaymentsSearchUrl = this.Url + "/payments/search"

    /// Where Api.registerTellConsentAsync posts.
    member this.TellConsentsUrl = this.Url + "/open-banking/tell-consents"

type BankProduct =
    { Url: string
      DisplayName: string
      /// e.g. "embedded", "savings", "client-money", "safeguarding"; Griffin adds classes over time.
      ProductClass: string
      FundingType: FundingType voption
      OwnerType: LegalPersonType voption
      Status: string voption
      /// POST here to open an account against the product.
      AccountsUrl: string
      Raw: string }

type Workflow =
    { Url: string
      DisplayName: string
      LegalPersonType: LegalPersonType
      Disabled: bool
      /// Claim types the workflow needs for its legal person type.
      RequiredClaimTypes: string list
      Raw: string }

type DeclineReason =
    { /// One of Griffin's decline-reason-code values, e.g. "applicant-under-16".
      Code: string
      Title: string
      Description: string
      ClaimTypes: string list }

type Decision =
    { Outcome: DecisionOutcome
      VerificationUrl: string voption
      LegalPersonUrl: string voption
      DecisionMaker: DecisionMaker voption
      Notes: string
      DeclineReasons: DeclineReason list
      CreatedAt: DateTimeOffset voption }

type LegalPerson =
    { Url: string
      DisplayName: string
      LegalPersonType: LegalPersonType
      Status: LegalPersonStatus voption
      ApplicationStatus: ApplicationStatus voption
      LatestDecision: Decision voption
      ClaimsUrl: string voption
      VerificationsUrl: string voption
      DecisionsUrl: string voption
      PayeesUrl: string voption
      CreatedAt: DateTimeOffset voption
      Raw: string }

type Verification =
    { Url: string
      LegalPersonUrl: string
      WorkflowUrl: string voption
      Status: VerificationStatus
      RiskRating: RiskRating voption
      ResourcesUrl: string voption
      CreatedAt: DateTimeOffset voption
      Raw: string }

/// A UK sort code and account number as Griffin lists them on an account; Griffin calls the sort code bank-id.
type BankAddress =
    { AccountHolder: string
      BankId: string
      BankIdCode: string
      AccountNumber: string
      AccountNumberCode: string }

module BankAddress =

    /// The address as a UkAccount, e.g. to pay into it.
    let toUkAccount (address: BankAddress) : UkAccount =
        { AccountHolder = address.AccountHolder
          SortCode = address.BankId
          AccountNumber = address.AccountNumber }

type BankAccount =
    { Url: string
      DisplayName: string
      Status: AccountStatus
      ProductType: BankProductType voption
      Balance: Money voption
      AvailableBalance: Money voption
      BankAddresses: BankAddress list
      OwnerUrl: string voption
      BeneficiaryUrl: string voption
      PaymentsUrl: string voption
      TransactionsUrl: string voption
      CloseUrl: string voption
      ProductUrl: string voption
      /// Inbound payments.
      AdmissionsUrl: string voption
      /// Outbound payment submissions.
      SubmissionsUrl: string voption
      RestrictionsUrl: string voption
      StatementsUrl: string voption
      /// Direct debit mandates the account pays.
      MandatesUrl: string voption
      PooledFunds: bool
      Restricted: bool
      CreatedAt: DateTimeOffset voption
      Raw: string }

    /// Card holds on the account (Api.listHoldsAsync).
    member this.HoldsUrl = this.Url + "/holds"

    /// A pooled account's members (Api.listPoolMembersAsync).
    member this.MembershipUrl = this.Url + "/membership"

    /// Where Api.updatePoolMembersAsync posts.
    member this.MembershipUpdatesUrl = this.Url + "/membership-updates"

type Payee =
    { Url: string
      LegalPersonUrl: string
      Account: UkAccount
      Status: PayeeStatus
      /// The Confirmation of Payee check registered with the payee, if any.
      CopRequestUrl: string voption
      Raw: string }

/// The answer to a Confirmation of Payee check: does the name match the account?
type CopCheck =
    { /// Register it with a payee (Api.createPayeeAsync) to record that the check was made.
      Url: string
      Status: CopStatus
      Result: CopResult
      Reason: CopReason voption
      /// The name on the account, for a close match.
      SuggestedName: string voption
      SuggestedAccountClassification: AccountClassification voption
      Raw: string }

/// The ID document and selfie check a customer completes in the browser, from a verification.
type IdvCheck =
    { ResourceId: string
      DisplayName: string
      LegalPersonUrl: string
      Status: IdvCheckStatus
      /// Send the customer here to complete the check. The sandbox gives a placeholder.
      Url: string
      ExpiresAt: DateTimeOffset voption }

/// A claim as stored on a legal person.
type ClaimRecord =
    { Url: string
      ClaimType: string
      CreatedAt: DateTimeOffset voption
      /// The claim with its fields, as Griffin returns it.
      Raw: string }

/// A freeze or block on a bank account.
type Restriction =
    { Url: string
      AccountUrl: string
      RestrictionType: RestrictionType voption
      Status: RestrictionStatus
      Reason: RestrictionReason voption
      LegalStatus: RestrictionLegalStatus voption
      LiftUrl: string voption
      UpdateUrl: string voption
      CreatedAt: DateTimeOffset voption
      Raw: string }

/// A statement of an account's transactions between two dates.
type Statement =
    { Url: string
      AccountUrl: string
      Format: StatementFormat
      StartDate: string
      EndDate: string
      /// DownloadUrl is set once Generated.
      Status: StatementStatus
      DownloadUrl: string voption
      CreatedAt: DateTimeOffset voption
      Raw: string }

/// A Bacs Direct Debit mandate: an originator's authority to collect from the account.
type Mandate =
    { Url: string
      /// The paying (Griffin) account.
      AccountUrl: string voption
      Status: MandateStatus
      Reference: string
      Frequency: CollectionFrequency voption
      Amount: MandateAmount voption
      /// The originator's account holder name, when Griffin knows it.
      CreditorName: string voption
      CancellationReason: MandateCancellationReason voption
      PaymentsUrl: string voption
      LastUsedAt: DateTimeOffset voption
      CreatedAt: DateTimeOffset voption
      Raw: string }

/// Where a payment goes.
type Creditor =
    /// A payee created for a legal person.
    | ToPayee of payeeUrl: string
    /// Another Griffin bank account in the organization (book transfer).
    | ToGriffinAccount of accountUrl: string
    /// Any UK account by sort code and account number.
    | ToUkAccount of UkAccount

type Payment =
    { Url: string
      Direction: PaymentDirection voption
      Amount: Money
      Reference: string voption
      SubmissionsUrl: string voption
      LatestSubmissionUrl: string voption
      CreatedAt: DateTimeOffset voption
      Raw: string }

type Submission =
    { Url: string
      PaymentUrl: string
      AccountUrl: string voption
      Status: SubmissionStatus
      StatusReason: string voption
      Scheme: PaymentScheme voption
      EndToEndId: string voption
      SchemeStatusCode: string voption
      CreatedAt: DateTimeOffset voption
      Raw: string }

/// An inbound payment arriving at an account.
type Admission =
    { Url: string
      AccountUrl: string
      PaymentUrl: string
      Status: AdmissionStatus
      Scheme: PaymentScheme voption
      CreatedAt: DateTimeOffset voption
      Raw: string }

type Transaction =
    { Url: string
      AccountUrl: string
      /// Always positive; Direction says which way it moved.
      BalanceChange: Money
      Direction: BalanceDirection
      /// The balance after this transaction.
      AccountBalance: Money
      Origin: TransactionOrigin voption
      Reference: string voption
      Description: string voption
      PaymentUrl: string voption
      EffectiveAt: DateTimeOffset voption
      PostedAt: DateTimeOffset voption
      Raw: string }

/// A registered webhook: where Griffin delivers the organization's events.
type WebhookEndpoint =
    { Url: string
      DestinationUrl: string
      Description: string voption
      State: WebhookState
      ActivateUrl: string voption
      DeactivateUrl: string voption
      TestUrl: string voption
      Raw: string }

[<RequireQualifiedAccess>]
type HoldStatus =
    | Placed
    | Released
    | Other of string

    static member private Names = [ Placed, "placed"; Released, "released" ]
    member this.Value = Wire.name HoldStatus.Names (function Other s -> s | Placed | Released -> "") this
    static member Parse text = Wire.parse HoldStatus.Names Other text

/// Funds reserved on an account, e.g. for an authorised card transaction that has not cleared.
type Hold =
    { Url: string
      AccountUrl: string
      Amount: Money
      Direction: BalanceDirection
      Status: HoldStatus
      /// e.g. "card-transaction".
      OriginType: string
      ReleaseReason: string voption
      ExpiresAt: DateTimeOffset voption
      /// Transactions the hold turned into, once released.
      TransactionUrls: string list
      CreatedAt: DateTimeOffset voption
      UpdatedAt: DateTimeOffset voption
      Raw: string }

/// A registered address as Companies House lists it.
type CompanyAddress =
    { BuildingNumber: string voption
      BuildingName: string voption
      StreetName: string voption
      City: string voption
      PostalCode: string voption
      CountryCode: string voption }

type CompanyDirector =
    { DisplayName: string
      AppointedOn: DateTime voption
      BirthMonth: int voption
      BirthYear: int voption
      CompaniesHouseUrl: string voption }

type PersonWithSignificantControl =
    { DisplayName: string
      GivenName: string voption
      Surname: string voption
      /// Companies House natures of control, e.g. "ownership-of-shares-75-to-100-percent".
      NaturesOfControl: string list
      BirthMonth: int voption
      BirthYear: int voption
      CompaniesHouseUrl: string voption }

/// A UK company from Companies House, as Griffin relays it (Api.lookupCompanyAsync).
type Company =
    { Number: string
      Name: string
      /// e.g. "active", "dissolved".
      Status: string voption
      /// e.g. "private-limited-company", "public-limited-company".
      CorporationType: string voption
      IncorporatedOn: DateTime voption
      Address: CompanyAddress voption
      SicCodes: string list
      AccountsOverdue: bool
      ConfirmationStatementOverdue: bool
      LatestAccountsOn: DateTime voption
      LatestConfirmationStatementOn: DateTime voption
      Directors: CompanyDirector list
      PersonsWithSignificantControl: PersonWithSignificantControl list
      Raw: string }

/// A legal person whose funds a pooled account holds.
type PoolMember =
    { LegalPersonUrl: string
      CreatedAt: DateTimeOffset voption }

[<RequireQualifiedAccess>]
type TellConsentScope =
    | Accounts
    | Payments
    | Other of string

    static member private Names = [ Accounts, "accounts"; Payments, "payments" ]
    member this.Value = Wire.name TellConsentScope.Names (function Other s -> s | Accounts | Payments -> "") this
    static member Parse text = Wire.parse TellConsentScope.Names Other text

/// A consent your customer gave a third-party provider through tell.money, to register with Griffin.
type TellConsentRequest =
    { ConsentId: string
      GatewayId: string
      Scope: TellConsentScope
      /// Open Banking permission names, e.g. "ReadAccountsDetail", "ReadBalances", "ReadTransactionsDetail".
      Permissions: string list
      /// The Griffin bank accounts the consent covers.
      BankAccountUrls: string list
      Expiry: DateTimeOffset voption }

/// A registered tell.money consent.
type TellConsent =
    { Url: string
      ConsentId: string
      GatewayId: string
      Scope: TellConsentScope
      /// e.g. "authorised", "revoked".
      Status: string
      Permissions: string list
      BankAccountUrls: string list
      Expiry: DateTimeOffset voption
      ConsumedAt: DateTimeOffset voption
      RevokedAt: DateTimeOffset voption
      CreatedAt: DateTimeOffset voption
      Raw: string }

    /// Where Api.revokeTellConsentAsync posts.
    member this.RevokeUrl = this.Url + "/actions/revoke"

/// A webhook verification key from Griffin's JWKS endpoint.
type WebhookKey =
    { KeyId: string
      Algorithm: string
      Curve: string
      /// Base64url EC public key coordinates.
      X: string
      Y: string }
