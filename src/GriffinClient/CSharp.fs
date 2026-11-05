namespace Griffin

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks

/// What GetValueOrThrow raises; Failure carries the GriffinFailure.
type GriffinException(failure: GriffinFailure) =
    inherit Exception(GriffinFailure.describe failure)

    member _.Failure = failure

/// For C#: reading Task<Result<_, GriffinFailure>> without matching on F# Result.
[<Extension; AbstractClass; Sealed>]
type GriffinResultExtensions =

    /// The value, or a GriffinException.
    [<Extension>]
    static member GetValueOrThrow(result: Result<'T, GriffinFailure>) : 'T =
        match result with
        | Ok value -> value
        | Error failure -> raise (GriffinException failure)

    /// Awaits the call, then the value or a GriffinException: await Api.getIndexAsync(client, ct).GetValueOrThrowAsync().
    [<Extension>]
    static member GetValueOrThrowAsync(result: Task<Result<'T, GriffinFailure>>) : Task<'T> =
        task {
            match! result with
            | Ok value -> return value
            | Error failure -> return raise (GriffinException failure)
        }

/// For C#: the Api functions that take an F# function or F# tuples, with .NET delegates and pairs instead.
[<AbstractClass; Sealed>]
type GriffinApi =

    /// Api.allPagesAsync with a delegate: GriffinApi.AllPagesAsync&lt;Transaction&gt;((link, ct) =>
    /// Api.listTransactionsAsync(client, link, ct), account.TransactionsUrl.Value, ct).
    static member AllPagesAsync
        (fetch: Func<string, CancellationToken, Task<Result<Page<'T>, GriffinFailure>>>, link: string, ct: CancellationToken)
        =
        Api.allPagesAsync (fun next token -> fetch.Invoke(next, token)) link ct

    /// Api.withQuery with name/value pairs.
    static member WithQuery(link: string, parameters: IEnumerable<KeyValuePair<string, string>>) =
        Api.withQuery link (parameters |> Seq.map (fun p -> p.Key, p.Value))

/// Api.Query as name/value pairs, for GriffinApi.WithQuery.
[<AbstractClass; Sealed>]
type GriffinQuery =

    static member private Pairs(parameters: (string * string) list) : IEnumerable<KeyValuePair<string, string>> =
        parameters |> Seq.map (fun (name, value) -> KeyValuePair<string, string>(name, value))

    static member PageSize(size: int) = GriffinQuery.Pairs(Api.Query.pageSize size)
    static member NewestFirst() = GriffinQuery.Pairs Api.Query.newestFirst
    static member OldestFirst() = GriffinQuery.Pairs Api.Query.oldestFirst
    static member Equal(field: string, value: string) = GriffinQuery.Pairs(Api.Query.equals field value)
    static member AnyOf(field: string, values: IEnumerable<string>) = GriffinQuery.Pairs(Api.Query.anyOf field values)
    static member AtLeast(field: string, value: string) = GriffinQuery.Pairs(Api.Query.atLeast field value)
