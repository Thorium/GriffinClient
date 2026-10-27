/// Readers over JsonElement and the serializer settings for request bodies. Optional readers treat a missing
/// property and JSON null alike.
module internal Griffin.Json

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Serialization

/// A required field is missing or has the wrong type.
exception InvalidJson of string

/// Request bodies: null fields are left out, so an optional field is ValueOption.toObj of its value.
let options = JsonSerializerOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)

let tryProp (name: string) (e: JsonElement) =
    if e.ValueKind <> JsonValueKind.Object then
        ValueNone
    else
        match e.TryGetProperty name with
        | true, v when v.ValueKind <> JsonValueKind.Null && v.ValueKind <> JsonValueKind.Undefined -> ValueSome v
        | _ -> ValueNone

let private ofKind kind (read: JsonElement -> 'T) name e =
    tryProp name e
    |> ValueOption.bind (fun v -> if v.ValueKind = kind then ValueSome(read v) else ValueNone)

let tryString name e = ofKind JsonValueKind.String _.GetString() name e

/// A string field read through parse, e.g. AccountStatus.Parse.
let tryParse (parse: string -> 'T) name e = tryString name e |> ValueOption.map parse

let tryBool name e =
    tryProp name e
    |> ValueOption.bind (fun v ->
        match v.ValueKind with
        | JsonValueKind.True -> ValueSome true
        | JsonValueKind.False -> ValueSome false
        | _ -> ValueNone)

let tryTimestamp name e =
    tryString name e
    |> ValueOption.bind (fun s ->
        match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, d -> ValueSome d
        | _ -> ValueNone)

let tryObject name e = ofKind JsonValueKind.Object id name e

/// A "yyyy-MM-dd" date.
let tryDate name e =
    tryString name e
    |> ValueOption.bind (fun s ->
        match DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, d -> ValueSome d
        | _ -> ValueNone)

let tryInt name e =
    ofKind JsonValueKind.Number id name e
    |> ValueOption.bind (fun v ->
        match v.TryGetInt32() with
        | true, n -> ValueSome n
        | _ -> ValueNone)

/// Griffin money: {"currency": "GBP", "value": "20.00"}. ValueNone when the value is not a decimal.
let tryMoney name e : Money voption =
    tryObject name e
    |> ValueOption.bind (fun m ->
        match tryString "currency" m, tryString "value" m with
        | ValueSome currency, ValueSome value ->
            match Decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture) with
            | true, v -> ValueSome { Currency = currency; Value = v }
            | _ -> ValueNone
        | _ -> ValueNone)

/// Elements of an array property; empty when the property is missing or null.
let array name e =
    match ofKind JsonValueKind.Array id name e with
    | ValueSome v -> v.EnumerateArray() |> List.ofSeq
    | ValueNone -> []

let strings name e =
    array name e
    |> List.choose (fun v -> if v.ValueKind = JsonValueKind.String then Some(v.GetString()) else None)

let required name (reader: string -> JsonElement -> 'T voption) e =
    match reader name e with
    | ValueSome v -> v
    | ValueNone -> raise (InvalidJson $"Missing or invalid field '{name}'")
