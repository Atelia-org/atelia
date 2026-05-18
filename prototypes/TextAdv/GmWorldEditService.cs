using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.TextAdv;

/// <summary>
/// GM 侧世界编辑工具集。当前代码直接调用这些方法；后续可用 MethodToolWrapper 暴露给 LLM GM Agent。
/// </summary>
internal sealed class GmWorldEditService {
    private const string WorldKey = "world";
    private const string GameKey = "game";
    private const string LocationsKey = "locations";
    private const string ItemsKey = "items";
    private const string ActorsKey = "actors";
    private const string InteractionsKey = "interactions";
    private const string TerminalPlayerActorId = "player";
    private const string NameKey = "name";
    private const string KindKey = "kind";
    private const string DescriptionKey = "description";
    private const string ProfileNoteKey = "profileNote";
    private const string ActiveKey = "active";
    private const string ExitsKey = "exits";
    private const string LocationIdKey = "locationId";
    private const string OwnerActorIdKey = "ownerActorId";
    private const string VisibilityKey = "visibility";
    private const string TargetKindKey = "targetKind";
    private const string TargetIdKey = "targetId";
    private const string ActionKindKey = "actionKind";
    private const string VisibleLabelKey = "visibleLabel";
    private const string PreconditionNoteKey = "preconditionNote";
    private const string EffectNoteKey = "effectNote";
    private const string TurnCostLedgerKey = "turnCost";
    private const string EffectScopeKey = "effectScope";
    private const string EffectSlotsKey = "effectSlots";
    private const string LastResolutionByActorKey = "lastResolutionByActor";
    private const string VisibleValue = "visible";

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
        if (!from.TryGetValue(out var fromLocation) || fromLocation is null) { return AteliaResult<string>.Failure(from.Error!); }

        var to = TryGetLocation(toLocationId);
        if (!to.TryGetValue(out var toLocation) || toLocation is null) { return AteliaResult<string>.Failure(to.Error!); }

        LinkOneWay(fromLocation, direction, toLocationId);
        if (reverseDirection is not null) {
            LinkOneWay(toLocation, reverseDirection, fromLocationId);
        }

        return reverseDirection is null
            ? $"{fromLocationId}.{direction}->{toLocationId}"
            : $"{fromLocationId}.{direction}->{toLocationId}; {toLocationId}.{reverseDirection}->{fromLocationId}";
    }

    internal AteliaResult<string> MoveActorTo(string actorId, string locationId) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        locationId = NormalizeRequired(locationId, nameof(locationId));

        var location = TryGetLocation(locationId);
        if (!location.IsSuccess) { return AteliaResult<string>.Failure(location.Error!); }

        var actor = TryGetActor(actorId);
        if (!actor.IsSuccess) { return AteliaResult<string>.Failure(actor.Error!); }

        actor.Value!.Upsert(LocationIdKey, locationId);
        return $"{actorId}->{locationId}";
    }

    internal AteliaResult<string> CreateItem(
        string itemId,
        string name,
        string description,
        string locationId
    ) {
        itemId = NormalizeRequired(itemId, nameof(itemId));
        name = NormalizeRequired(name, nameof(name));
        description = NormalizeRequired(description, nameof(description));
        locationId = NormalizeRequired(locationId, nameof(locationId));

        var location = TryGetLocation(locationId);
        if (!location.IsSuccess) { return AteliaResult<string>.Failure(location.Error!); }

        var items = GetOrCreateWorldDict(ItemsKey);
        if (items.TryGet(itemId, out DurableDict<string>? _)) {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.ItemAlreadyExists",
                    $"Item '{itemId}' 已存在。"
                )
            );
        }

        var item = _root.Revision.CreateDict<string>();
        item.Upsert(NameKey, name);
        item.Upsert(DescriptionKey, description);
        item.Upsert(LocationIdKey, locationId);
        item.Upsert(VisibilityKey, VisibleValue);
        items.Upsert(itemId, item);
        return itemId;
    }

    internal AteliaResult<string> MoveItemToActor(
        string itemId,
        string actorId
    ) {
        itemId = NormalizeRequired(itemId, nameof(itemId));
        actorId = NormalizeRequired(actorId, nameof(actorId));

        var itemResult = TryGetItem(itemId);
        if (!itemResult.TryGetValue(out var item) || item is null) { return AteliaResult<string>.Failure(itemResult.Error!); }

        var actorResult = ValidateTargetExists("actor", actorId);
        if (!actorResult.IsSuccess) { return AteliaResult<string>.Failure(actorResult.Error!); }

        item.Upsert(OwnerActorIdKey, actorId);
        item.Remove(LocationIdKey);
        return $"{itemId}->actor:{actorId}";
    }

    internal AteliaResult<string> PlaceItemAtLocation(
        string itemId,
        string locationId
    ) {
        itemId = NormalizeRequired(itemId, nameof(itemId));
        locationId = NormalizeRequired(locationId, nameof(locationId));

        var itemResult = TryGetItem(itemId);
        if (!itemResult.TryGetValue(out var item) || item is null) { return AteliaResult<string>.Failure(itemResult.Error!); }

        var location = TryGetLocation(locationId);
        if (!location.IsSuccess) { return AteliaResult<string>.Failure(location.Error!); }

        item.Upsert(LocationIdKey, locationId);
        item.Remove(OwnerActorIdKey);
        return $"{itemId}->location:{locationId}";
    }

    internal AteliaResult<string> UpdateItem(
        string itemId,
        string? name,
        string? description
    ) {
        itemId = NormalizeRequired(itemId, nameof(itemId));
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        if (name is null && description is null) {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.ItemUpdateEmpty",
                    "gm_update_item 至少要更新 name 或 description 之一。"
                )
            );
        }

        var itemResult = TryGetItem(itemId);
        if (!itemResult.TryGetValue(out var item) || item is null) { return AteliaResult<string>.Failure(itemResult.Error!); }

        if (name is not null) {
            item.Upsert(NameKey, name);
        }

        if (description is not null) {
            item.Upsert(DescriptionKey, description);
        }

        return itemId;
    }

    internal AteliaResult<string> CreateNpc(
        string actorId,
        string name,
        string profileNote,
        string locationId
    ) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        name = NormalizeRequired(name, nameof(name));
        profileNote = NormalizeRequired(profileNote, nameof(profileNote));
        locationId = NormalizeRequired(locationId, nameof(locationId));

        var location = TryGetLocation(locationId);
        if (!location.IsSuccess) { return AteliaResult<string>.Failure(location.Error!); }

        var actors = GetOrCreateWorldDict(ActorsKey);
        if (actors.TryGet(actorId, out DurableDict<string>? _)) {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.ActorAlreadyExists",
                    $"Actor '{actorId}' 已存在。"
                )
            );
        }

        var actor = _root.Revision.CreateDict<string>();
        actor.Upsert(KindKey, "npc");
        actor.Upsert(NameKey, name);
        actor.Upsert(ProfileNoteKey, profileNote);
        actor.Upsert(LocationIdKey, locationId);
        actor.Upsert(VisibilityKey, VisibleValue);
        actor.Upsert(ActiveKey, true);
        actors.Upsert(actorId, actor);
        return actorId;
    }

    internal AteliaResult<string> AddInteraction(
        string interactionId,
        string targetRef,
        string actionKind,
        string visibleLabel,
        string preconditionNote,
        string effectNote,
        int turnCost,
        string effectScope,
        string effectSlots
    ) {
        interactionId = NormalizeRequired(interactionId, nameof(interactionId));
        targetRef = NormalizeRequired(targetRef, nameof(targetRef));
        actionKind = InteractionActionKinds.Canonicalize(NormalizeRequired(actionKind, nameof(actionKind)));
        visibleLabel = NormalizeRequired(visibleLabel, nameof(visibleLabel));
        preconditionNote = NormalizeRequired(preconditionNote, nameof(preconditionNote));
        effectNote = NormalizeRequired(effectNote, nameof(effectNote));
        effectScope = NormalizeRequired(effectScope, nameof(effectScope)).ToLowerInvariant();
        effectSlots = NormalizeRequired(effectSlots, nameof(effectSlots));

        if (!TryParseTargetRef(targetRef, out var targetKind, out var targetId)) {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.InvalidTargetRef",
                    $"target_ref '{targetRef}' 无效；格式应为 location:<id>、item:<id> 或 actor:<id>。"
                )
            );
        }

        var targetResult = ValidateTargetExists(targetKind, targetId);
        if (!targetResult.IsSuccess) { return AteliaResult<string>.Failure(targetResult.Error!); }

        var effectSlotList = ParseEffectSlots(effectSlots);
        if (turnCost < 0) {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.InvalidTurnCost",
                    $"turn_cost '{turnCost}' 无效；必须是 0 或正整数。"
                )
            );
        }

        if (!IsAllowedEffectScope(effectScope)) {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.InvalidEffectScope",
                    $"effect_scope '{effectScope}' 无效；允许 self / room / adjacent-room / scene。"
                )
            );
        }

        if (effectSlotList.Count == 0) {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.InvalidEffectSlots",
                    "effect_slots 不能为空；至少应包含 immediate / turn-end / per-turn-end / on-completion 之一。"
                )
            );
        }

        if (effectSlotList.Any(static slot => !IsAllowedEffectSlot(slot))) {
            var invalid = string.Join(", ", effectSlotList.Where(static slot => !IsAllowedEffectSlot(slot)));
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.InvalidEffectSlots",
                    $"存在无效 effect_slot: {invalid}。允许 immediate / turn-end / per-turn-end / on-completion。"
                )
            );
        }

        if (effectSlotList.Contains(GameSimulation.ImmediateEffectSlot, StringComparer.Ordinal)
            && !string.Equals(effectScope, GameSimulation.SelfEffectScope, StringComparison.Ordinal)) {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.ImmediateScopeMismatch",
                    "只有 effect_scope=self 的交互才能包含 immediate 槽位。"
                )
            );
        }

        if (turnCost == 0 && effectSlotList.Any(
            static slot => string.Equals(slot, GameSimulation.PerTurnEndEffectSlot, StringComparison.Ordinal)
                    || string.Equals(slot, GameSimulation.OnCompletionEffectSlot, StringComparison.Ordinal)
        )) {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.ZeroTurnCompletionMismatch",
                    "turn_cost=0 的交互不能使用 per-turn-end 或 on-completion 槽位。"
                )
            );
        }

        var interactions = GetOrCreateWorldDict(InteractionsKey);
        if (interactions.TryGet(interactionId, out DurableDict<string>? _)) {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.InteractionAlreadyExists",
                    $"Interaction '{interactionId}' 已存在。"
                )
            );
        }

        var interaction = _root.Revision.CreateDict<string>();
        interaction.Upsert(TargetKindKey, targetKind);
        interaction.Upsert(TargetIdKey, targetId);
        interaction.Upsert(ActionKindKey, actionKind);
        interaction.Upsert(VisibleLabelKey, visibleLabel);
        interaction.Upsert(PreconditionNoteKey, preconditionNote);
        interaction.Upsert(EffectNoteKey, effectNote);
        interaction.Upsert(TurnCostLedgerKey, turnCost);
        interaction.Upsert(EffectScopeKey, effectScope);
        var effectSlotDict = _root.Revision.CreateDict<string>();
        for (var i = 0; i < effectSlotList.Count; i++) {
            effectSlotDict.Upsert($"slot-{i:D2}", effectSlotList[i]);
        }
        interaction.Upsert(EffectSlotsKey, effectSlotDict);
        interaction.Upsert(VisibilityKey, VisibleValue);
        interactions.Upsert(interactionId, interaction);

        return interactionId;
    }

    internal AteliaResult<string> SetInteractionVisibility(
        string interactionId,
        string visibility
    ) {
        interactionId = NormalizeRequired(interactionId, nameof(interactionId));
        visibility = NormalizeRequired(visibility, nameof(visibility));

        if (!IsAllowedVisibility(visibility)) {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.InvalidVisibility",
                    $"Visibility '{visibility}' 无效；允许 visible / hidden / discovered。"
                )
            );
        }

        var interactions = GetOrCreateWorldDict(InteractionsKey);
        if (!interactions.TryGet(interactionId, out DurableDict<string>? interaction) || interaction is null) {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.InteractionNotFound",
                    $"Interaction '{interactionId}' 不存在。"
                )
            );
        }

        interaction.Upsert(VisibilityKey, visibility.ToLowerInvariant());
        return $"{interactionId}={visibility.ToLowerInvariant()}";
    }

    internal AteliaResult<string> SetActorResolution(
        string actorId,
        string summary
    ) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        summary = NormalizeRequired(summary, nameof(summary));

        var actor = TryGetActor(actorId);
        if (!actor.IsSuccess) { return AteliaResult<string>.Failure(actor.Error!); }

        var lastResolutionByActor = GetOrCreateGameDict(LastResolutionByActorKey);
        lastResolutionByActor.Upsert(actorId, summary);
        return actorId;
    }

    internal AteliaResult<string> SetVisibility(
        string targetRef,
        string visibility
    ) {
        targetRef = NormalizeRequired(targetRef, nameof(targetRef));
        visibility = NormalizeRequired(visibility, nameof(visibility));

        if (!IsAllowedVisibility(visibility)) {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.InvalidVisibility",
                    $"Visibility '{visibility}' 无效；允许 visible / hidden / discovered。"
                )
            );
        }

        if (!TryParseTargetRef(targetRef, out var targetKind, out var targetId)) {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.InvalidTargetRef",
                    $"target_ref '{targetRef}' 无效；格式应为 item:<id> 或 actor:<id>。"
                )
            );
        }

        DurableDict<string>? target = null;
        if (string.Equals(targetKind, "item", StringComparison.OrdinalIgnoreCase)) {
            var items = GetOrCreateWorldDict(ItemsKey);
            _ = items.TryGet(targetId, out target);
        }
        else if (string.Equals(targetKind, "actor", StringComparison.OrdinalIgnoreCase)) {
            var actors = GetOrCreateWorldDict(ActorsKey);
            _ = actors.TryGet(targetId, out target);
        }
        else {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.UnsupportedVisibilityTarget",
                    $"gm_set_visibility 仅支持 item 或 actor，不支持 '{targetKind}'。"
                )
            );
        }

        if (target is null) {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.TargetNotFound",
                    $"Target '{targetRef}' 不存在。"
                )
            );
        }

        target.Upsert(VisibilityKey, visibility.ToLowerInvariant());
        return $"{targetKind}:{targetId}={visibility.ToLowerInvariant()}";
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
        return ToToolResult(MoveActorTo(TerminalPlayerActorId, location_id), "moved player");
    }

    [Tool("gm_move_actor", "把指定 Actor 移动到指定 Location。多主体回合中优先使用；终端玩家 ActorId 是 player。")]
    public ValueTask<ToolExecuteResult> MoveActorAsync(
        [ToolParam("目标 ActorId；终端玩家为 player。")] string actor_id,
        [ToolParam("目标 LocationId。")] string location_id,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        return ToToolResult(MoveActorTo(actor_id, location_id), "moved actor");
    }

    [Tool("gm_create_item", "创建一个玩家可见的 Item，并放置在指定 Location。")]
    public ValueTask<ToolExecuteResult> CreateItemAsync(
        [ToolParam("新的 ItemId，建议使用小写 ASCII、数字和连字符。")] string item_id,
        [ToolParam("玩家可见的物品名称。未识别前应使用不剧透的通用叫法。")] string name,
        [ToolParam("玩家可见的物品描述。未识别前不要写出隐藏身份。")] string description,
        [ToolParam("物品所在 LocationId。")] string location_id,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        return ToToolResult(CreateItem(item_id, name, description, location_id), "created item");
    }

    [Tool("gm_create_npc", "创建一个玩家可见的 NPC Actor，并放置在指定 Location。")]
    public ValueTask<ToolExecuteResult> CreateNpcAsync(
        [ToolParam("新的 ActorId，建议使用小写 ASCII、数字和连字符。")] string actor_id,
        [ToolParam("玩家可见的 NPC 名称。")] string name,
        [ToolParam("NPC 的简短 GM profile note；应包含玩家可见气质，不包含需要隐藏的秘密。")] string profile_note,
        [ToolParam("NPC 所在 LocationId。")] string location_id,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        return ToToolResult(CreateNpc(actor_id, name, profile_note, location_id), "created npc");
    }

    [Tool("gm_update_item", "更新一个已存在 Item 的玩家可见名称或描述。用于识别后改名，或在拿起、翻动、清洗后刷新描述。")]
    public ValueTask<ToolExecuteResult> UpdateItemAsync(
        [ToolParam("目标 ItemId。")] string item_id,
        [ToolParam("新的玩家可见名称；若本次不改名，传 null。")] string? name,
        [ToolParam("新的玩家可见描述；若本次不改描述，传 null。")] string? description,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        return ToToolResult(UpdateItem(item_id, name, description), "updated item");
    }

    [Tool("gm_move_item_to_actor", "把 Item 转移到 Actor 持有。用于 take / give / pick-up 等交互。")]
    public ValueTask<ToolExecuteResult> MoveItemToActorAsync(
        [ToolParam("目标 ItemId。")] string item_id,
        [ToolParam("持有该物品的 ActorId；当前终端玩家为 player。")] string actor_id,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        return ToToolResult(MoveItemToActor(item_id, actor_id), "moved item to actor");
    }

    [Tool("gm_place_item_at_location", "把 Item 放置到指定 Location。用于 drop / place / reveal 等交互。")]
    public ValueTask<ToolExecuteResult> PlaceItemAtLocationAsync(
        [ToolParam("目标 ItemId。")] string item_id,
        [ToolParam("目标 LocationId。")] string location_id,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        return ToToolResult(PlaceItemAtLocation(item_id, location_id), "placed item at location");
    }

    [Tool("gm_add_interaction", "给 Location、Item 或 Actor 增加一个玩家可见的交互 affordance。")]
    public ValueTask<ToolExecuteResult> AddInteractionAsync(
        [ToolParam("新的 InteractionId，建议使用小写 ASCII、数字和连字符。它会暴露给玩家，不要在未确认前剧透隐藏真相。")] string interaction_id,
        [ToolParam("交互目标，格式为 location:<locationId>、item:<itemId> 或 actor:<actorId>。")] string target_ref,
        [ToolParam("交互类型，例如 inspect / take / use / open / listen。")] string action_kind,
        [ToolParam("玩家可见的交互标签，例如“检查贝壳边缘”。未确认前不要剧透真实身份。")] string visible_label,
        [ToolParam("基础前置条件说明；若没有特别条件，写 none。")] string precondition_note,
        [ToolParam("交互效果的简短 GM note；首版可用自然语言。")] string effect_note,
        [ToolParam("该交互消耗几个回合。0=顺手动作，1=结束本回合，N=进入持续工作。")] int turn_cost,
        [ToolParam("效果触达范围：self / room / adjacent-room / scene。")] string effect_scope,
        [ToolParam("效果槽位，使用逗号分隔：immediate,turn-end,per-turn-end,on-completion。")] string effect_slots,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        return ToToolResult(
            AddInteraction(
                interaction_id,
                target_ref,
                action_kind,
                visible_label,
                precondition_note,
                effect_note,
                turn_cost,
                effect_scope,
                effect_slots
            ),
            "added interaction"
        );
    }

    [Tool("gm_set_visibility", "设置 Item 或 Actor 的可见性。visibility 只能是 visible / hidden / discovered。")]
    public ValueTask<ToolExecuteResult> SetVisibilityAsync(
        [ToolParam("目标，格式为 item:<itemId> 或 actor:<actorId>。")] string target_ref,
        [ToolParam("新的可见性：visible / hidden / discovered。")] string visibility,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        return ToToolResult(SetVisibility(target_ref, visibility), "set visibility");
    }

    [Tool("gm_set_interaction_visibility", "设置 Interaction affordance 的可见性。visibility 只能是 visible / hidden / discovered。")]
    public ValueTask<ToolExecuteResult> SetInteractionVisibilityAsync(
        [ToolParam("目标 InteractionId。")] string interaction_id,
        [ToolParam("新的可见性：visible / hidden / discovered。")] string visibility,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        return ToToolResult(SetInteractionVisibility(interaction_id, visibility), "set interaction visibility");
    }

    [Tool("gm_set_actor_resolution", "为指定 active player actor 写入下一回合可见的私有结算反馈。多主体 summary 阶段应为每个 active player actor 调用一次。")]
    public ValueTask<ToolExecuteResult> SetActorResolutionAsync(
        [ToolParam("目标 ActorId；终端玩家为 player。")] string actor_id,
        [ToolParam("该 actor 下一回合可见的 1 到 4 句中文私有结算反馈。不能泄露此 actor 不应知道的信息。")] string summary,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        return ToToolResult(SetActorResolution(actor_id, summary), "set actor resolution");
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

    private AteliaResult<DurableDict<string>> TryGetItem(string itemId) {
        var items = GetOrCreateWorldDict(ItemsKey);
        if (!items.TryGet(itemId, out DurableDict<string>? item) || item is null) {
            return AteliaResult<DurableDict<string>>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.ItemNotFound",
                    $"Item '{itemId}' 不存在。"
                )
            );
        }

        return item;
    }

    private AteliaResult<DurableDict<string>> TryGetActor(string actorId) {
        var actors = GetOrCreateWorldDict(ActorsKey);
        if (!actors.TryGet(actorId, out DurableDict<string>? actor) || actor is null) {
            return AteliaResult<DurableDict<string>>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.ActorNotFound",
                    $"Actor '{actorId}' 不存在。"
                )
            );
        }

        return actor;
    }

    private DurableDict<string> GetOrCreateWorldDict(string key) {
        var world = _root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        if (world.TryGet(key, out DurableDict<string>? dict) && dict is not null) { return dict; }

        dict = _root.Revision.CreateDict<string>();
        world.Upsert(key, dict);
        return dict;
    }

    private DurableDict<string> GetOrCreateGameDict(string key) {
        var game = _root.GetOrThrow<DurableDict<string>>(GameKey)!;
        if (game.TryGet(key, out DurableDict<string>? dict) && dict is not null) { return dict; }

        dict = _root.Revision.CreateDict<string>();
        game.Upsert(key, dict);
        return dict;
    }

    private AteliaResult<string> ValidateTargetExists(string targetKind, string targetId) {
        if (string.Equals(targetKind, "location", StringComparison.OrdinalIgnoreCase)) {
            var location = TryGetLocation(targetId);
            return location.IsSuccess ? targetId : AteliaResult<string>.Failure(location.Error!);
        }

        if (string.Equals(targetKind, "item", StringComparison.OrdinalIgnoreCase)) {
            var items = GetOrCreateWorldDict(ItemsKey);
            if (items.TryGet(targetId, out DurableDict<string>? _)) { return targetId; }

            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.ItemNotFound",
                    $"Item '{targetId}' 不存在。"
                )
            );
        }

        if (string.Equals(targetKind, "actor", StringComparison.OrdinalIgnoreCase)) {
            var actors = GetOrCreateWorldDict(ActorsKey);
            if (actors.TryGet(targetId, out DurableDict<string>? _)) { return targetId; }

            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.Gm.ActorNotFound",
                    $"Actor '{targetId}' 不存在。"
                )
            );
        }

        return AteliaResult<string>.Failure(
            new TextAdvError(
                "TextAdv.Gm.UnsupportedTargetKind",
                $"不支持的 target kind: '{targetKind}'。"
            )
        );
    }

    private static bool TryParseTargetRef(string targetRef, out string targetKind, out string targetId) {
        var separatorIndex = targetRef.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex >= targetRef.Length - 1) {
            targetKind = string.Empty;
            targetId = string.Empty;
            return false;
        }

        targetKind = targetRef[..separatorIndex].Trim();
        targetId = targetRef[(separatorIndex + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(targetKind) && !string.IsNullOrWhiteSpace(targetId);
    }

    private static bool IsAllowedVisibility(string visibility)
        => string.Equals(visibility, "visible", StringComparison.OrdinalIgnoreCase)
            || string.Equals(visibility, "hidden", StringComparison.OrdinalIgnoreCase)
            || string.Equals(visibility, "discovered", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedEffectScope(string effectScope)
        => string.Equals(effectScope, GameSimulation.SelfEffectScope, StringComparison.OrdinalIgnoreCase)
            || string.Equals(effectScope, GameSimulation.RoomEffectScope, StringComparison.OrdinalIgnoreCase)
            || string.Equals(effectScope, GameSimulation.AdjacentRoomEffectScope, StringComparison.OrdinalIgnoreCase)
            || string.Equals(effectScope, GameSimulation.SceneEffectScope, StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedEffectSlot(string effectSlot)
        => string.Equals(effectSlot, GameSimulation.ImmediateEffectSlot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(effectSlot, GameSimulation.TurnEndEffectSlot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(effectSlot, GameSimulation.PerTurnEndEffectSlot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(effectSlot, GameSimulation.OnCompletionEffectSlot, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ParseEffectSlots(string effectSlots)
        => effectSlots
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(static slot => slot.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private void UpsertActorLocation(string actorId, string locationId) {
        var actors = GetOrCreateWorldDict(ActorsKey);
        if (!actors.TryGet(actorId, out DurableDict<string>? actor) || actor is null) { return; }

        actor.Upsert(LocationIdKey, locationId);
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
