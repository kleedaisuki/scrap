using System.Text.Json;
using System.Text;

namespace Scrap.Protocol.Tests;

public sealed class EnvelopeTests
{
    [Fact]
    public void RequestCreate_ProducesObjectParamsAndCamelCaseWireNames()
    {
        ProtocolRequest request = ProtocolRequest.Create(
            "opaque-id",
            ProtocolMethods.RecordSearch,
            new SearchRequest("s", "Q", SearchMode.Regex, CaseSensitivity.Sensitive, 5));

        string json = JsonSerializer.Serialize(request, ProtocolJson.Options);

        Assert.Contains("\"protocolVersion\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"caseSensitivity\":\"sensitive\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchCaseSensitivity", json, StringComparison.Ordinal);
        request.EnsureValid();
    }

    [Fact]
    public void ResponseFactories_EnforceResultErrorShapeAndRoundTripResult()
    {
        var record = new RecordSummaryDto(
            "scope",
            "key",
            RecordPresentation.Masked,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1);
        ProtocolResponse success = ProtocolResponse.Success("id", new RecordSetResult(record, true));
        ProtocolResponse failure = ProtocolResponse.Failure(
            "id",
            new ProtocolError(ProtocolErrorCodes.RecordNotFound, "Record does not exist."));

        Assert.True(success.GetResult<RecordSetResult>().Created);
        RemoteProtocolException exception = Assert.Throws<RemoteProtocolException>(
            () => failure.GetResult<RecordSetResult>());
        Assert.Equal(ProtocolErrorCodes.RecordNotFound, exception.ErrorCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ResponseEnsureValid_RejectsAnythingOtherThanExclusiveResultOrError(
        bool hasResult,
        bool hasError)
    {
        JsonElement? result = hasResult ? ProtocolJson.ToElement(new EmptyResult()) : null;
        ProtocolError? error = hasError ? new ProtocolError("error", "message") : null;
        var response = new ProtocolResponse(1, "id", result, error);

        ProtocolException exception = Assert.Throws<ProtocolException>(response.EnsureValid);

        Assert.Equal(ProtocolErrorCodes.InvalidRequest, exception.ErrorCode);
    }

    [Fact]
    public void RequestEnsureValid_RejectsNonObjectParams()
    {
        var request = new ProtocolRequest(1, "id", "method", ProtocolJson.ToElement("not-object"));

        ProtocolException exception = Assert.Throws<ProtocolException>(request.EnsureValid);

        Assert.Equal(ProtocolErrorCodes.InvalidRequest, exception.ErrorCode);
    }

    [Fact]
    public void JsonDeserializer_AcceptsAddedFieldsButRejectsIntegerEnums()
    {
        const string compatible = """
            {"scope":"s","query":"q","mode":"fuzzy","caseSensitivity":"insensitive","limit":10,"future":true}
            """;
        SearchRequest request = ProtocolJson.Deserialize<SearchRequest>(Encoding.UTF8.GetBytes(compatible));

        Assert.Equal(SearchMode.Fuzzy, request.Mode);
        const string integerEnum = """
            {"scope":"s","query":"q","mode":1,"caseSensitivity":"insensitive","limit":10}
            """;
        Assert.Throws<JsonException>(() =>
            ProtocolJson.Deserialize<SearchRequest>(Encoding.UTF8.GetBytes(integerEnum)));
    }

    [Fact]
    public void MetadataResults_NeverSerializeRecordValue()
    {
        var summary = new RecordSummaryDto(
            "scope",
            "key",
            RecordPresentation.Plain,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1);

        string json = JsonSerializer.Serialize(new RecordListResult([summary]), ProtocolJson.Options);

        Assert.DoesNotContain("value", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllPublishedMethodNames_AreUniqueAndIncludeAtomicRecordRename()
    {
        string[] methods =
        [
            ProtocolMethods.ScopeList,
            ProtocolMethods.ScopeCreate,
            ProtocolMethods.ScopeRename,
            ProtocolMethods.ScopeDelete,
            ProtocolMethods.RecordGet,
            ProtocolMethods.RecordSet,
            ProtocolMethods.RecordRename,
            ProtocolMethods.RecordDelete,
            ProtocolMethods.RecordList,
            ProtocolMethods.RecordSearch,
            ProtocolMethods.DaemonPing,
            ProtocolMethods.DaemonVersion,
            ProtocolMethods.DaemonShutdown,
        ];

        Assert.Equal(methods.Length, methods.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("record.rename", methods, StringComparer.Ordinal);
    }

    [Fact]
    public void RecordListParams_OldScopeOnlyPayloadUsesDefaultPageSize()
    {
        RecordListParams parameters = ProtocolJson.Deserialize<RecordListParams>(
            Encoding.UTF8.GetBytes("""{"scope":"production"}"""));

        Assert.Equal("production", parameters.Scope);
        Assert.Null(parameters.AfterKey);
        Assert.Equal(ProtocolConstants.DefaultRecordListPageSize, parameters.Limit);
        Assert.True(ProtocolConstants.MaxRecordListPageSize > parameters.Limit);
    }

    [Fact]
    public void RecordListPagination_RoundTripsOrdinalCursor()
    {
        var parameters = new RecordListParams("scope", "API_TOKEN", 25);
        var result = new RecordListResult([], "api_token");

        string parametersJson = Encoding.UTF8.GetString(ProtocolJson.Serialize(parameters));
        string resultJson = Encoding.UTF8.GetString(ProtocolJson.Serialize(result));

        Assert.Contains("\"afterKey\":\"API_TOKEN\"", parametersJson, StringComparison.Ordinal);
        Assert.Contains("\"limit\":25", parametersJson, StringComparison.Ordinal);
        Assert.Contains("\"nextCursor\":\"api_token\"", resultJson, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordListFinalPage_OmitsNullCursorForWireCompatibility()
    {
        string json = Encoding.UTF8.GetString(ProtocolJson.Serialize(new RecordListResult([])));

        Assert.DoesNotContain("nextCursor", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_RejectsIsolatedSurrogateInRecordValue()
    {
        string invalidValue = new(['\uD800']);
        var parameters = new RecordSetParams("scope", "key", invalidValue);

        JsonException exception = Assert.Throws<JsonException>(() => ProtocolJson.Serialize(parameters));

        Assert.DoesNotContain(invalidValue, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToElement_RejectsIsolatedSurrogateInNestedRecordKey()
    {
        string invalidKey = new(['\uDFFF']);
        var summary = new RecordSummaryDto(
            "scope",
            invalidKey,
            RecordPresentation.Masked,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1);

        Assert.Throws<JsonException>(() => ProtocolJson.ToElement(new RecordListResult([summary])));
    }

    [Theory]
    [InlineData("D800")]
    [InlineData("DFFF")]
    public void Deserialize_RejectsEscapedUnpairedSurrogateInNestedValue(string surrogate)
    {
        string json = $$"""
            {"scope":"s","key":"k","value":"\u{{surrogate}}","presentation":"masked"}
            """;

        Assert.Throws<JsonException>(() =>
            ProtocolJson.Deserialize<RecordSetParams>(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Deserialize_AcceptsEscapedSurrogatePairWithoutReplacement()
    {
        const string json = """
            {"scope":"s","key":"k","value":"\uD83D\uDE00","presentation":"plain"}
            """;

        RecordSetParams parameters = ProtocolJson.Deserialize<RecordSetParams>(Encoding.UTF8.GetBytes(json));

        Assert.Equal("😀", parameters.Value);
    }

    [Fact]
    public void DeserializeElement_RejectsEscapedUnpairedSurrogateInEnvelopeParams()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{"scope":"s","key":"\uD800","value":"v","presentation":"masked"}""");

        ProtocolException exception = Assert.Throws<ProtocolException>(() =>
            ProtocolJson.DeserializeElement<RecordSetParams>(document.RootElement));

        Assert.Equal(ProtocolErrorCodes.InvalidJson, exception.ErrorCode);
    }
}
