using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace LootGoblin.Services;

public class PartyMember
{
    public string Name { get; set; } = "";
    public bool IsMounted { get; set; }
    public ushort MountId { get; set; }
    public bool IsFlying { get; set; }
    public bool IsPillionRider { get; set; }
    public Vector3 Position { get; set; }
    public bool IsInSameZone { get; set; }
    public bool IsReady { get; set; }
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

    public void UpdatePartyStatus()
    {
        if (!_clientState.IsLoggedIn)
        {
            SetState(PartyCoordinationState.Error, "Not logged in.");
            return;
        }

        PartyMembers.Clear();
        var localPlayer = _objectTable.LocalPlayer;
        if (localPlayer == null)
        {
            SetState(PartyCoordinationState.Error, "Local player not found.");
            return;
        }

        // Add local player
        PartyMembers.Add(CreatePartyMember(localPlayer, localPlayer));

        // Add party members from object table
        for (int i = 0; i < _partyList.Length; i++)
        {
            var member = _partyList[i];
            if (member == null) continue;

            // Find object in object table by name
            foreach (var obj in _objectTable)
            {
                if (obj != null && obj.Name.TextValue == member.Name.TextValue && obj.Address != localPlayer.Address)
                {
                    PartyMembers.Add(CreatePartyMember(obj, localPlayer));
                    break;
                }
            }
        }

        // Check if all members are ready
        AllMembersMounted = PartyMembers.All(m => m.IsMounted);
        AllMembersReady = PartyMembers.All(m => m.IsReady);

        var currentMounted = PartyMembers.Count(m => m.IsMounted);
        if (PartyMembers.Count != lastLoggedMemberCount || currentMounted != lastLoggedMountedCount)
        {
            lastLoggedMemberCount = PartyMembers.Count;
            lastLoggedMountedCount = currentMounted;
            _plugin.AddDebugLog($"Party: {PartyMembers.Count} members, {currentMounted} mounted");
        }
    }

    public bool WaitForAllMounted(int timeoutSeconds = 60)
    {
        if (PartyMembers.Count <= 1)
        {
            _plugin.AddDebugLog("Solo: No party coordination needed.");
            return true;
        }

        SetState(PartyCoordinationState.WaitingForMounts, $"Waiting for all {PartyMembers.Count} members to mount...");

        var startTime = DateTime.Now;
        while ((DateTime.Now - startTime).TotalSeconds < timeoutSeconds)
        {
            UpdatePartyStatus();
            if (AllMembersMounted)
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

        var allInSameZone = PartyMembers.All(m => m.IsInSameZone);

        if (!allInSameZone)
        {
            var notInZone = PartyMembers.Where(m => !m.IsInSameZone).Select(m => m.Name);
            _plugin.AddDebugLog($"Members not in same zone: {string.Join(", ", notInZone)}");
        }

        return allInSameZone;
    }

    private unsafe PartyMember CreatePartyMember(IGameObject obj, IGameObject localPlayer)
    {
        var member = new PartyMember
        {
            Name = obj.Name.TextValue,
            Position = obj.Position,
            IsInSameZone = true, // Visible in object table = same zone
        };

        try
        {
            var chara = (Character*)obj.Address;
            member.IsMounted = chara->IsMounted();
            member.MountId = chara->Mount.MountId;

            if (member.IsMounted)
            {
                member.IsFlying = obj.Position.Y > localPlayer.Position.Y + 2.0f;
                member.IsPillionRider = false; // TODO: Implement proper pillion detection
            }

            // If mounted, consider ready (all mounts can fly in zones where flying is unlocked)
            member.IsReady = member.IsMounted;
        }
        catch
        {
            // Mount data inaccessible
        }

        return member;
    }

    private void SetState(PartyCoordinationState state, string detail)
    {
        State = state;
        StateDetail = detail;
        _plugin.AddDebugLog($"Party state: {state} - {detail}");
    }
}
