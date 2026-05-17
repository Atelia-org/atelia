using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.TextAdv;

/// <summary>
/// GM 侧世界编辑工具集。当前代码直接调用这些方法；后续可用 MethodToolWrapper 暴露给 LLM GM Agent。
/// </summary>
internal sealed class GmWorldEditService {
    private const string WorldKey = "world";
    private const string LocationsKey = "locations";
    private const string PlayerKey = "player";
    private const string PlayerLocationKey = "location";
    private const string NameKey = "name";
    private const string DescriptionKey = "description";
    private const string ExitsKey = "exits";

    private readonly DurableDict<string> _root;

    public GmWorldEditService(DurableDict<string> root) {
        _root = root ?? throw new ArgumentNullException(nameof(root));
    }

    internal AteliaResult<string> CreateLocation(
        string locationId,
        string name,
        string description
    ) {
        locationId = NormalizeRequired(locationId, nameof(locationId));
        name = NormalizeRequired(name, nameof(name));
        description = NormalizeRequired(description, nameof(description));

        var locations = GetLocations();
        if (locations.TryGet(locationId, out DurableDict<string>? _)) {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.LocationAlreadyExists",
                    $"Location '{locationId}' 已存在。"
                )
            );
        }

        var location = _root.Revision.CreateDict<string>();
        location.Upsert(NameKey, name);
        location.Upsert(DescriptionKey, description);
        location.Upsert(ExitsKey, _root.Revision.CreateDict<string>());
        locations.Upsert(locationId, location);

        return locationId;
    }

    internal AteliaResult<string> LinkLocations(
        string fromLocationId,
        string direction,
        string toLocationId,
        string? reverseDirection
    ) {
        fromLocationId = NormalizeRequired(fromLocationId, nameof(fromLocationId));
        direction = NormalizeRequired(direction, nameof(direction));
        toLocationId = NormalizeRequired(toLocationId, nameof(toLocationId));
        reverseDirection = string.IsNullOrWhiteSpace(reverseDirection) ? null : reverseDirection.Trim();

        var from = TryGetLocation(fromLocationId);
        if (!from.TryGetValue(out var fromLocation) || fromLocation is null) {
            return AteliaResult<string>.Failure(from.Error!);
        }

        var to = TryGetLocation(toLocationId);
        if (!to.TryGetValue(out var toLocation) || toLocation is null) {
            return AteliaResult<string>.Failure(to.Error!);
        }

        LinkOneWay(fromLocation, direction, toLocationId);
        if (reverseDirection is not null) {
            LinkOneWay(toLocation, reverseDirection, fromLocationId);
        }

        return reverseDirection is null
            ? $"{fromLocationId}.{direction}->{toLocationId}"
            : $"{fromLocationId}.{direction}->{toLocationId}; {toLocationId}.{reverseDirection}->{fromLocationId}";
    }

    internal AteliaResult<string> MovePlayerTo(string locationId) {
        locationId = NormalizeRequired(locationId, nameof(locationId));
        var location = TryGetLocation(locationId);
        if (!location.IsSuccess) {
            return AteliaResult<string>.Failure(location.Error!);
        }

        var player = _root.GetOrThrow<DurableDict<string>>(PlayerKey)!;
        player.Upsert(PlayerLocationKey, locationId);
        return locationId;
    }

    [Tool("gm_create_location", "创建一个新的 Location。location_id 必须稳定且唯一。")]
    public ValueTask<ToolExecuteResult> CreateLocationAsync(
        [ToolParam("新的 LocationId，建议使用小写 ASCII、数字和连字符。")] string location_id,
        [ToolParam("玩家可见的地点名称。")] string name,
        [ToolParam("玩家进入该地点时可见的地点描述。")] string description,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        return ToToolResult(CreateLocation(location_id, name, description), "created location");
    }

    [Tool("gm_link_locations", "建立两个 Location 之间的出口连接；reverse_direction 可为空。")]
    public ValueTask<ToolExecuteResult> LinkLocationsAsync(
        [ToolParam("起点 LocationId。")] string from_location_id,
        [ToolParam("从起点看见的出口方向。")] string direction,
        [ToolParam("目标 LocationId。")] string to_location_id,
        [ToolParam("从目标返回起点的方向；不需要反向边时传 null。")] string? reverse_direction,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        return ToToolResult(LinkLocations(from_location_id, direction, to_location_id, reverse_direction), "linked locations");
    }

    [Tool("gm_move_player", "把当前玩家移动到指定 Location。")]
    public ValueTask<ToolExecuteResult> MovePlayerAsync(
        [ToolParam("目标 LocationId。")] string location_id,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        return ToToolResult(MovePlayerTo(location_id), "moved player");
    }

    private AteliaResult<DurableDict<string>> TryGetLocation(string locationId) {
        var locations = GetLocations();
        if (!locations.TryGet(locationId, out DurableDict<string>? location) || location is null) {
            return AteliaResult<DurableDict<string>>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.LocationNotFound",
                    $"Location '{locationId}' 不存在。"
                )
            );
        }

        return location;
    }

    private DurableDict<string> GetLocations() {
        var world = _root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        return world.GetOrThrow<DurableDict<string>>(LocationsKey)!;
    }

    private static void LinkOneWay(DurableDict<string> location, string direction, string targetLocationId) {
        var exits = location.GetOrThrow<DurableDict<string>>(ExitsKey)!;
        exits.Upsert(direction, targetLocationId);
    }

    private static string NormalizeRequired(string value, string parameterName) {
        if (string.IsNullOrWhiteSpace(value)) { throw new ArgumentException("Value cannot be null or whitespace.", parameterName); }
        return value.Trim();
    }

    private static ValueTask<ToolExecuteResult> ToToolResult(AteliaResult<string> result, string action) {
        if (!result.TryGetValue(out var value) || string.IsNullOrWhiteSpace(value)) {
            return ValueTask.FromResult(
                new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    result.Error?.Message ?? $"{action} failed."
                )
            );
        }

        return ValueTask.FromResult(
            new ToolExecuteResult(
                ToolExecutionStatus.Success,
                $"{action}: {value}"
            )
        );
    }
}
