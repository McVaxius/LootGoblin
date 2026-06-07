using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace LootGoblin.Services;

public class PartyMember
{
    public string Name { get; set; } = "";
    public ulong ContentId { get; set; }
    public uint WorldId { get; set; }
    public uint EntityId { get; set; }
    public uint TerritoryId { get; set; }
    public bool IsLocalPlayer { get; set; }
    public bool IsMounted { get; set; }
    public ushort MountId { get; set; }
    public bool IsFlying { get; set; }
    public bool IsPillionRider { get; set; }
    public Vector3 Position { get; set; }
    public PartyTerritoryStatus TerritoryStatus { get; set; }
    public bool IsInSameTerritory => TerritoryStatus == PartyTerritoryStatus.Same;
    public bool IsInSameZone => IsInSameTerritory;
    public bool IsLoaded { get; set; }
    public bool HasPosition { get; set; }
    public PartyPositionSource PositionSource { get; set; }
    public bool IsReady { get; set; }

    public PartyProximityMember ToProximityMember()
        => new(
            Name,
            ContentId,
            WorldId,
            EntityId,
            IsLocalPlayer,
            TerritoryStatus,
            IsLoaded,
            HasPosition,
            Position,
            PositionSource);
}

public enum PartyCoordinationState
{
    Idle,
    CheckingParty,
    WaitingForMounts,
    AllReady,
    Error,
}

public class PartyService : IDisposable
{
    private readonly Plugin _plugin;
    private readonly IPluginLog _log;
    private readonly IPartyList _partyList;
    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;

    public PartyCoordinationState State { get; private set; } = PartyCoordinationState.Idle;
    public string StateDetail { get; private set; } = "";
    public List<PartyMember> PartyMembers { get; } = new();
    public bool AllMembersMounted { get; private set; }
    public bool AllMembersReady { get; private set; }
    public int LastValidMemberCount { get; private set; }

    private int lastLoggedMemberCount;
    private int lastLoggedMountedCount;

    public PartyService(Plugin plugin, IPartyList partyList, IObjectTable objectTable, IClientState clientState, ICondition condition, IPluginLog log)
    {
        _plugin = plugin;
        _partyList = partyList;
        _objectTable = objectTable;
        _clientState = clientState;
        _condition = condition;
        _log = log;
    }

    public void Dispose() { }

    public bool UpdatePartyStatus()
    {
        if (!_clientState.IsLoggedIn)
        {
            ResetPartySnapshot(clearLastValid: true);
            SetState(PartyCoordinationState.Idle, "Not logged in.");
            return false;
        }

        if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51])
        {
            ResetPartySnapshot();
            SetState(PartyCoordinationState.Idle, "Loading...");
            return false;
        }

        PartyMembers.Clear();
        var localPlayer = _objectTable.LocalPlayer;
        if (localPlayer == null)
        {
            // During area transitions, local player can be temporarily null - don't error
            ResetPartySnapshot();
            SetState(PartyCoordinationState.Idle, "Loading...");
            return false;
        }

        var localTerritoryId = _clientState.TerritoryType;
        var sawLocalPlayer = false;
        for (int i = 0; i < _partyList.Length; i++)
        {
            var member = _partyList[i];
            if (member == null) continue;

            var snapshot = CreatePartyMember(member, localPlayer, localTerritoryId);
            sawLocalPlayer |= snapshot.IsLocalPlayer;
            PartyMembers.Add(snapshot);
        }

        if (!sawLocalPlayer)
            PartyMembers.Insert(0, CreateLocalPlayerMember(localPlayer, localTerritoryId));

        AllMembersMounted = PartyMembers.Count > 0 &&
                            PartyMembers.All(member =>
                                PartyGateSemantics.IsLoadedSameTerritoryMounted(
                                    member.IsLoaded,
                                    member.TerritoryStatus,
                                    member.IsMounted));
        AllMembersReady = PartyMembers.Count > 0 &&
                          PartyMembers.All(member =>
                              PartyGateSemantics.IsLoadedSameTerritory(
                                  member.IsLoaded,
                                  member.TerritoryStatus) &&
                              member.IsReady);
        LastValidMemberCount = PartyMembers.Count;

        var currentMounted = PartyMembers.Count(member =>
            PartyGateSemantics.IsLoadedSameTerritoryMounted(
                member.IsLoaded,
                member.TerritoryStatus,
                member.IsMounted));
        if (PartyMembers.Count != lastLoggedMemberCount || currentMounted != lastLoggedMountedCount)
        {
            lastLoggedMemberCount = PartyMembers.Count;
            lastLoggedMountedCount = currentMounted;
            _plugin.AddDebugLog($"Party: {PartyMembers.Count} members, {currentMounted} mounted");
        }

        return true;
    }

    public bool WaitForAllMounted(int timeoutSeconds = 60)
    {
        if (!UpdatePartyStatus())
        {
            SetState(PartyCoordinationState.Error, "Party snapshot unavailable.");
            return false;
        }

        if (PartyMembers.Count <= 1)
        {
            _plugin.AddDebugLog("Solo: No party coordination needed.");
            return true;
        }

        SetState(PartyCoordinationState.WaitingForMounts, $"Waiting for all {PartyMembers.Count} members to mount...");

        var startTime = DateTime.Now;
        while ((DateTime.Now - startTime).TotalSeconds < timeoutSeconds)
        {
            if (UpdatePartyStatus() && AllMembersMounted)
            {
                SetState(PartyCoordinationState.AllReady, "All party members mounted!");
                return true;
            }

            if (_condition[ConditionFlag.InCombat])
            {
                SetState(PartyCoordinationState.Error, "Combat detected, waiting aborted.");
                return false;
            }

            System.Threading.Thread.Sleep(1000);
        }

        SetState(PartyCoordinationState.Error, $"Timeout waiting for mounts after {timeoutSeconds}s.");
        return false;
    }

    public bool VerifyAllInSameZone()
    {
        if (PartyMembers.Count <= 1) return true;

        var allInSameZone = PartyMembers.All(member =>
            PartyGateSemantics.IsLoadedSameTerritory(member.IsLoaded, member.TerritoryStatus));

        if (!allInSameZone)
        {
            var notInZone = PartyMembers
                .Where(member => !PartyGateSemantics.IsLoadedSameTerritory(member.IsLoaded, member.TerritoryStatus))
                .Select(member => member.Name);
            _plugin.AddDebugLog($"Members not loaded in same zone: {string.Join(", ", notInZone)}");
        }

        return allInSameZone;
    }

    private PartyMember CreatePartyMember(IPartyMember partyMember, IGameObject localPlayer, uint localTerritoryId)
    {
        var gameObject = partyMember.GameObject;
        var territoryId = partyMember.Territory.RowId;
        var territoryStatus = territoryId == 0
            ? PartyTerritoryStatus.Unknown
            : territoryId == localTerritoryId
                ? PartyTerritoryStatus.Same
                : PartyTerritoryStatus.Different;

        var member = new PartyMember
        {
            Name = partyMember.Name.TextValue,
            ContentId = partyMember.ContentId,
            WorldId = partyMember.World.RowId,
            EntityId = partyMember.EntityId,
            TerritoryId = territoryId,
            TerritoryStatus = territoryStatus,
            IsLocalPlayer =
                partyMember.EntityId == localPlayer.EntityId ||
                gameObject?.EntityId == localPlayer.EntityId,
            IsLoaded = gameObject != null,
            HasPosition = gameObject != null ||
                          territoryStatus == PartyTerritoryStatus.Same && IsFinite(partyMember.Position),
            Position = gameObject?.Position ?? partyMember.Position,
            PositionSource = gameObject != null
                ? PartyPositionSource.DirectActor
                : territoryStatus == PartyTerritoryStatus.Same && IsFinite(partyMember.Position)
                    ? PartyPositionSource.PartyList
                    : PartyPositionSource.None,
        };

        if (gameObject != null)
            PopulateLoadedActorState(member, gameObject, localPlayer);

        return member;
    }

    private PartyMember CreateLocalPlayerMember(IGameObject localPlayer, uint localTerritoryId)
    {
        var member = new PartyMember
        {
            Name = localPlayer.Name.TextValue,
            EntityId = localPlayer.EntityId,
            TerritoryId = localTerritoryId,
            TerritoryStatus = PartyTerritoryStatus.Same,
            IsLocalPlayer = true,
            IsLoaded = true,
            HasPosition = true,
            Position = localPlayer.Position,
            PositionSource = PartyPositionSource.DirectActor,
        };

        PopulateLoadedActorState(member, localPlayer, localPlayer);
        return member;
    }

    private static unsafe void PopulateLoadedActorState(PartyMember member, IGameObject gameObject, IGameObject localPlayer)
    {
        try
        {
            var chara = (Character*)gameObject.Address;
            member.IsMounted = chara->IsMounted();
            member.MountId = chara->Mount.MountId;

            if (member.IsMounted)
            {
                member.IsFlying = gameObject.Position.Y > localPlayer.Position.Y + 2.0f;
                member.IsPillionRider = false;
            }

            member.IsReady = member.IsMounted;
        }
        catch
        {
            // Loaded actor exists, but mount data can be temporarily inaccessible.
        }
    }

    private static bool IsFinite(Vector3 position)
        => float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);

    private void SetState(PartyCoordinationState state, string detail)
    {
        State = state;
        StateDetail = detail;
        _plugin.AddDebugLog($"Party state: {state} - {detail}");
    }

    private void ResetPartySnapshot(bool clearLastValid = false)
    {
        PartyMembers.Clear();
        AllMembersMounted = false;
        AllMembersReady = false;
        if (clearLastValid)
            LastValidMemberCount = 0;
    }
}
