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
}
