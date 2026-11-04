/// Bodies and signatures stay on Griffin's host.
module RedirectTests

open System
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open Griffin

/// A local host that answers one request.
type private Host(answer: string) =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    let mutable called = false
    do listener.Start()

    let served =
        task {
            try
                use! socket = listener.AcceptTcpClientAsync()
                called <- true
                use stream = socket.GetStream()
                let buffer = Array.zeroCreate 16384
                let! _ = stream.ReadAsync(buffer, 0, buffer.Length)
                let bytes = Encoding.ASCII.GetBytes answer
                do! stream.WriteAsync(bytes, 0, bytes.Length)
            with
            | :? SocketException
            | :? ObjectDisposedException -> ()
        }

    member _.Port = (listener.LocalEndpoint :?> IPEndPoint).Port
    member _.Called = called
    member _.Served: Task = served

    interface IDisposable with
        member _.Dispose() = listener.Stop()

[<Fact>]
let ``the client made here stops at a redirect, as an error`` () : Task =
    task {
        use other = new Host "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"
        use first =
            new Host $"HTTP/1.1 307 Moved\r\nLocation: http://127.0.0.1:{other.Port}/stolen\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
        use http = GriffinClient.createHttpClient ()
        let config = { GriffinConfig.create "the-key" with BaseUrl = $"http://127.0.0.1:{first.Port}" }
        let client = { Config = config; Http = http }
        let! result = Api.postAsync client "/v0/anything" {| name = "Ada" |} id CancellationToken.None
        do! first.Served
        Assert.True(Result.isError result)
        Assert.False other.Called
    }
