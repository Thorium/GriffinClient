using System.Net;
using System.Text;
using System.Text.Json;
using Griffin;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Xunit;

namespace GriffinClient.CSharpTests;

/// <summary>Answers each request with respond(path and query), and records what was sent.</summary>
sealed class FakeGriffin(Func<string, (int Status, string Json)> respond) : HttpMessageHandler
{
    public List<(string Method, string Target, Dictionary<string, string> Headers, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        var target = request.RequestUri!.PathAndQuery;
        Calls.Add((request.Method.Method, target, headers, body));
        var (status, json) = respond(target);
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

public class CSharpUsageTests
{
    static readonly CancellationToken Ct = CancellationToken.None;

    const string Organization =
        """{"display-name":"Test Org","organization-mode":"test-mode","organization-legal-persons-url":"/organizations/og.1/legal-persons","organization-bank-accounts-url":"/organizations/og.1/bank/accounts","organization-bank-account-products-url":"/organizations/og.1/bank/products","organization-workflows-url":"/organizations/og.1/workflows"}""";

    static string Transaction(string id, string direction) =>
        $$"""{"account-transaction-url":"/bank/transactions/{{id}}","account-url":"/bank/accounts/ba.1","balance-change":{"currency":"GBP","value":"1.00"},"balance-change-direction":"{{direction}}","account-balance":{"currency":"GBP","value":"10.00"},"transaction-origin-type":"payment"}""";

    static Griffin.GriffinClient ClientWith(FakeGriffin handler) =>
        new(GriffinConfig.Create("g-test-key"), new HttpClient(handler));

    [Fact]
    public async Task Reads_the_organization_and_every_page_with_delegates()
    {
        var handler = new FakeGriffin(target => target switch
        {
            "/index" => (200, """{"organization-url":"/organizations/og.1"}"""),
            "/organizations/og.1" => (200, Organization),
            "/bank/accounts/ba.1/transactions?page%5Bsize%5D=2" =>
                (200, $$$"""{"account-transactions":[{{{Transaction("tr.1", "credit")}}},{{{Transaction("tr.2", "debit")}}}],"links":{"prev":null,"next":"/bank/accounts/ba.1/transactions?page[after]=x"}}"""),
            _ => (200, $$$"""{"account-transactions":[{{{Transaction("tr.3", "credit")}}}],"links":{"prev":null,"next":null}}"""),
        });

        var client = ClientWith(handler);
        var org = await Api.getOwnOrganizationAsync(client, Ct).GetValueOrThrowAsync();
        Assert.Equal(OrganizationMode.TestMode, org.Mode);

        var first = GriffinApi.WithQuery("/bank/accounts/ba.1/transactions", [KeyValuePair.Create("page[size]", "2")]);

        var all = await GriffinApi
            .AllPagesAsync<Transaction>((link, token) => Api.listTransactionsAsync(client, link, token), first, Ct)
            .GetValueOrThrowAsync();

        Assert.Equal(["/bank/transactions/tr.1", "/bank/transactions/tr.2", "/bank/transactions/tr.3"], all.Items.Select(t => t.Url));
        Assert.Equal(BalanceDirection.Debit, all.Items.ElementAt(1).Direction);
        Assert.Empty(all.Unparsed);
    }

    [Fact]
    public async Task A_Griffin_error_becomes_a_GriffinException_carrying_the_failure()
    {
        var handler = new FakeGriffin(_ =>
            (404, """{"errors":[{"title":"Resource not found","status":"404","detail":"Could not find the resource"}]}"""));

        var ex = await Assert.ThrowsAsync<GriffinException>(() => Api.getIndexAsync(ClientWith(handler), Ct).GetValueOrThrowAsync());

        Assert.Contains("404", ex.Message);
        Assert.Contains("Resource not found", ex.Message);
        var apiError = Assert.IsType<GriffinFailure.ApiError>(ex.Failure);
        Assert.Equal(404, apiError.httpStatus);
    }

    [Fact]
    public async Task Creates_a_legal_person_from_an_array_of_claims_and_default_for_none()
    {
        var handler = new FakeGriffin(_ =>
            (201, """{"legal-person-url":"/legal-persons/lp.1","display-name":"Ada Example","legal-person-type":"individual"}"""));

        Claim[] claims =
        [
            Claims.individualIdentity("Ada", default, "Example", new DateTime(1985, 6, 15)),
            Claims.individualIdentity("Bea", FSharpValueOption<string>.Some("Q"), "Example", new DateTime(1990, 1, 2)),
            Claims.taxResidencies(["GB"]),
            Claims.employment(Employment.NewEmployed("information-technology", FSharpValueOption<string>.Some("software-engineer"))),
            Claims.custom("alternative-number", new Dictionary<string, object> { ["telephone-number"] = "+442012345678" }),
        ];

        var person = await Api
            .createLegalPersonAsync(ClientWith(handler), "/organizations/og.1/legal-persons", "Ada Example", LegalPersonType.Individual, claims, Ct)
            .GetValueOrThrowAsync();

        Assert.Equal(LegalPersonType.Individual, person.LegalPersonType);

        var sent = JsonDocument.Parse(handler.Calls.Single().Body).RootElement.GetProperty("claims").EnumerateArray().ToArray();
        Assert.False(sent[0].TryGetProperty("middle-name", out _));
        Assert.Equal("Q", sent[1].GetProperty("middle-name").GetString());
        Assert.Equal("GB", sent[2].GetProperty("tax-residencies")[0].GetString());
        Assert.Equal("software-engineer", sent[3].GetProperty("occupation").GetString());
        Assert.Equal("+442012345678", sent[4].GetProperty("telephone-number").GetString());
        Assert.Equal("alternative-number", sent[4].GetProperty("claim-type").GetString());
    }

    [Fact]
    public async Task A_signer_from_a_delegate_signs_requests()
    {
        var handler = new FakeGriffin(_ => (200, """{"organization-url":"/organizations/og.1"}"""));
        var signedBases = new List<string>();

        var signer = RequestSigner.Create("hs.csharp", (data, token) =>
        {
            signedBases.Add(Encoding.UTF8.GetString(data));
            return Task.FromResult(new byte[64]);
        });

        var defaults = GriffinConfig.Create("g-test-key");
        var config = new GriffinConfig(defaults.ApiKey, defaults.BaseUrl, defaults.ApiVersion, FSharpValueOption<RequestSigner>.Some(signer), defaults.SignatureLifetime);

        await Api.getIndexAsync(new Griffin.GriffinClient(config, new HttpClient(handler)), Ct).GetValueOrThrowAsync();

        Assert.Contains("keyid=\"hs.csharp\"", handler.Calls.Single().Headers["Signature-Input"]);
        Assert.Contains("\"@path\": /index", Assert.Single(signedBases));
    }

    [Fact]
    public async Task An_invalid_UK_account_is_refused_before_sending()
    {
        var handler = new FakeGriffin(_ => (201, "{}"));
        var creditor = Creditor.NewToUkAccount(new UkAccount("Ada", "60-01-30", "12345678"));

        var ex = await Assert.ThrowsAsync<GriffinException>(() =>
            Api.createPaymentAsync(ClientWith(handler), "/bank/accounts/ba.1/payments", creditor, Money.Gbp(5m), default, Ct)
                .GetValueOrThrowAsync());

        Assert.True(ex.Failure.IsInvalidRequest);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public void A_webhook_request_from_header_pairs_is_checked_case_insensitively()
    {
        var request = Webhook.WebhookRequest.Create(
            "POST",
            "hooks.example.com",
            "/griffin/webhook",
            "",
            [KeyValuePair.Create("SIGNATURE-INPUT", "x=(\"date\");keyid=\"k\";expires=1")],
            "{}"u8.ToArray());

        var result = Webhook.verifyWithKeys(FSharpList<WebhookKey>.Empty, request, DateTimeOffset.UtcNow);

        Assert.True(result.IsError);
        var missing = Assert.IsType<WebhookRejection.MissingHeader>(result.ErrorValue);
        Assert.Equal("signature", missing.name);
    }
}
