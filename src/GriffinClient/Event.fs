namespace Griffin

open System
open System.Text.Json

/// What an event says happened. Known event types are typed; the rest keep their payload as JSON text.
type EventPayload =
    | TransactionCreated of Transaction
    | PaymentCreated of Payment
    | SubmissionCreated of Submission
    | SubmissionUpdated of Submission
    | AdmissionCreated of Admission
    | AdmissionUpdated of Admission
    | AccountStatusCreated of accountUrl: string * status: AccountStatus
    | AccountStatusUpdated of accountUrl: string * status: AccountStatus
    | VerificationCreated of Verification
    | VerificationUpdated of Verification
    | DecisionCreated of Decision
    /// The payload needs a newer Griffin-Version than the request (or webhook) used.
    | Unavailable of requiredApiVersion: string
    /// Another event type, or a known one whose payload did not parse; payload is its JSON text.
    | Other of eventType: string * payload: string

/// A change Griffin recorded, as listed by Api.listEventsAsync and delivered to webhooks.
type Event =
    { Url: string
      /// e.g. "transaction-created", "submission-updated".
      EventType: string
      CreatedAt: DateTimeOffset voption
      Payload: EventPayload
      Raw: string }

module Event =

    let private payloadOf (eventType: string) (payload: JsonElement) =
        let requiredVersion =
            Json.tryObject "meta" payload
            |> ValueOption.bind (Json.tryObject "required")
            |> ValueOption.bind (Json.tryString "api-version")

        match requiredVersion with
        | ValueSome version -> Unavailable version
        | ValueNone ->
            let status () = Json.required "account-status" (Json.tryParse AccountStatus.Parse) payload
            let account () = Json.required "account-url" Json.tryString payload

            try
                match eventType with
                | "transaction-created" -> TransactionCreated(Parse.transaction payload)
                | "payment-created" -> PaymentCreated(Parse.payment payload)
                | "submission-created" -> SubmissionCreated(Parse.submission payload)
                | "submission-updated" -> SubmissionUpdated(Parse.submission payload)
                | "admission-created" -> AdmissionCreated(Parse.admission payload)
                | "admission-updated" -> AdmissionUpdated(Parse.admission payload)
                | "account-status-created" -> AccountStatusCreated(account (), status ())
                | "account-status-updated" -> AccountStatusUpdated(account (), status ())
                | "verification-created" -> VerificationCreated(Parse.verification payload)
                | "verification-updated" -> VerificationUpdated(Parse.verification payload)
                | "decision-created" -> DecisionCreated(Parse.decision payload)
                | other -> Other(other, payload.GetRawText())
            with Json.InvalidJson _ ->
                Other(eventType, payload.GetRawText())

    let ofJson (e: JsonElement) : Event =
        let eventType = Json.required "event-type" Json.tryString e

        { Url = Json.required "event-url" Json.tryString e
          EventType = eventType
          CreatedAt = Json.tryTimestamp "created-at" e
          Payload =
            match Json.tryProp "event-payload" e with
            | ValueSome payload -> payloadOf eventType payload
            | ValueNone -> Other(eventType, "null")
          Raw = e.GetRawText() }

    /// Parses an event's JSON text, e.g. a webhook body after Webhook.verifyAsync.
    let parse (json: string) : Result<Event, GriffinFailure> =
        try
            use doc = JsonDocument.Parse json
            Ok(ofJson doc.RootElement)
        with
        | :? JsonException as e -> Error(InvalidResponse e.Message)
        | Json.InvalidJson message -> Error(InvalidResponse message)
