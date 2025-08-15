using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GroupManager = FFXIVClientStructs.FFXIV.Client.Game.Group.GroupManager;

namespace Inviter
{
    public class Inviter : IDalamudPlugin
    {
        public static Inviter Plugin;
        internal Localizer localizer;
        public WindowSystem WindowSystem = new("Inviter");
        public ConfigurationWindow ConfigurationWindow;
        public Configuration Config { get; private set; }

        private static readonly Lock LockInviteObj = new();
        private long NextInviteAt = 0;

        private delegate IntPtr GetUIBaseDelegate();
        private delegate IntPtr GetUIModuleDelegate(IntPtr basePtr);

        private delegate char EasierProcessInviteDelegate(Int64 a1, Int64 a2, Int16 world_id, IntPtr name, char a5);
        private readonly EasierProcessInviteDelegate _EasierProcessInvite;

        private delegate char EasierProcessEurekaInviteDelegate(Int64 a1, Int64 a2);
        private readonly EasierProcessEurekaInviteDelegate _EasierProcessEurekaInvite;

        private delegate char EasierProcessCIDDelegate(nint a1, nint a2);
        private readonly Hook<EasierProcessCIDDelegate> easierProcessCIDHook;

        private readonly GetUIModuleDelegate GetUIModule;
        private delegate IntPtr GetMagicUIDelegate(IntPtr basePtr);
        private IntPtr uiModule;
        private Int64 uiInvite;
        private readonly Dictionary<string, long> name2CID = [];
        internal TimedEnable timedRecruitment;
        private readonly List<uint> eureka_territories = [732, 763, 795, 827, 920, 975, 1252];
        [PluginService]
        public static ICommandManager CmdManager { get; private set; } = null!;
        [PluginService]
        public static ISigScanner SigScanner { get; private set; } = null!;
        [PluginService]
        public static IDalamudPluginInterface Interface { get; private set; } = null!;
        [PluginService]
        public static IGameGui GameGui { get; private set; } = null!;
        [PluginService]
        public static IChatGui ChatGui { get; private set; } = null!;
        [PluginService]
        public static IToastGui ToastGui { get; private set; } = null!;
        [PluginService]
        public static IClientState ClientState { get; private set; } = null!;
        [PluginService]
        public static ICondition Condition { get; private set; } = null!;
        [PluginService]
        public static IDataManager Data { get; private set; } = null!;
        [PluginService]
        public static IGameInteropProvider Hook { get; private set; } = null!;
        [PluginService]
        public static IPluginLog PluginLog { get; private set; } = null!;

        public Inviter()
        {
            Plugin = this;
            Config = Interface.GetPluginConfig() as Configuration ?? new Configuration();
            localizer = new Localizer(Config.UILanguage);

            var InviteToPartyByName = SigScanner.ScanText("E8 ?? ?? ?? ?? EB B0 CC");
            _EasierProcessInvite = Marshal.GetDelegateForFunctionPointer<EasierProcessInviteDelegate>(InviteToPartyByName);

            var InviteToPartyInInstanceByContentId = SigScanner.ScanText("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 48 8B 83 ?? ?? ?? ?? 48 85 C0");
            _EasierProcessEurekaInvite = Marshal.GetDelegateForFunctionPointer<EasierProcessEurekaInviteDelegate>(InviteToPartyInInstanceByContentId);

            var easierProcessCIDPtr = SigScanner.ScanText("40 53 48 83 EC 20 48 8B DA 48 8D 0D ?? ?? ?? ?? 8B 52 10 E8 ?? ?? ?? ?? 48 85 C0 74 30");
            easierProcessCIDHook = Hook.HookFromAddress<EasierProcessCIDDelegate>(easierProcessCIDPtr, EasierProcessCIDDetour);
            easierProcessCIDHook.Enable();

            InitUi();
            PluginLog.Info("===== I N V I T E R =====");
            PluginLog.Info($"InviteToPartyByName address {InviteToPartyByName:X}");
            PluginLog.Info($"InviteToPartyInInstance address {InviteToPartyInInstanceByContentId:X}");
            PluginLog.Info($"Process CID address {easierProcessCIDPtr:X}");
            PluginLog.Info($"uiModule address {uiModule:X}");
            PluginLog.Info($"uiInvite address {uiInvite:X}");

            CmdManager.AddHandler("/xinvite", new CommandInfo(CommandHandler)
            {
                HelpMessage = "/xinvite - open the inviter panel.\n" +
                    "/xinvite <on/off/toggle> - turn the auto invite on/off.\n" +
                    "/xinvite <minutes> - enable temporary auto invite for certain amount of time in minutes.\n" +
                    "/xinvite <minutes> <attempts> - enable temporary auto invite for certain amount of time in minutes and finish it after certain amount of invite attempts.\n"
            });
            ConfigurationWindow = new();
            WindowSystem.AddWindow(ConfigurationWindow);
            Interface.UiBuilder.Draw += WindowSystem.Draw;
            Interface.UiBuilder.OpenMainUi += ConfigurationWindow.Toggle;
            Interface.UiBuilder.OpenConfigUi += ConfigurationWindow.Toggle;

            ChatGui.ChatMessage += Chat_OnChatMessage;
            ClientState.TerritoryChanged += TerritoryChanged;
            timedRecruitment = new();
        }

        public void Dispose()
        {
            timedRecruitment.FinishTimer();
            ChatGui.ChatMessage -= Chat_OnChatMessage;
            ClientState.TerritoryChanged -= TerritoryChanged;
            Interface.UiBuilder.Draw -= WindowSystem.Draw;
            Interface.UiBuilder.OpenMainUi -= ConfigurationWindow.Toggle;
            Interface.UiBuilder.OpenConfigUi -= ConfigurationWindow.Toggle;
            WindowSystem.RemoveAllWindows();
            CmdManager.RemoveHandler("/xinvite");
            easierProcessCIDHook.Dispose();
        }

        private void TerritoryChanged(ushort e)
        {
            name2CID.Clear();
        }

        public unsafe void CommandHandler(string command, string arguments)
        {
            var args = arguments.Trim().Replace("\"", string.Empty);

            if (string.IsNullOrEmpty(args) || args.Equals("config", StringComparison.OrdinalIgnoreCase))
            {
                ConfigurationWindow.Toggle();
                return;
            }
            else if (args == "on")
            {
                Config.Enable = true;
                ToastGui.ShowQuest(string.Format(localizer.Localize("Auto invite is turned on for \"{0}\""), Config.TextPattern),
                    new QuestToastOptions
                    {
                        DisplayCheckmark = true,
                        PlaySound = true
                    });
                Config.Save();
            }
            else if (args == "off")
            {
                Config.Enable = false;
                ToastGui.ShowQuest(localizer.Localize("Auto invite is turned off"),
                    new QuestToastOptions
                    {
                        DisplayCheckmark = true,
                        PlaySound = true
                    });
                Config.Save();
            }
            else if (args == "party")
            {
                Log($"MemberCount:{GroupManager.Instance()->MainGroup.MemberCount}");
                Log($"LeaderIndex:{GroupManager.Instance()->MainGroup.PartyLeaderIndex}");
                if (GroupManager.Instance()->MainGroup.MemberCount > 0)
                    Log($"LeaderName:{ConvertSpanToString(GroupManager.Instance()->MainGroup.GetPartyMemberByIndex((int)GroupManager.Instance()->MainGroup.PartyLeaderIndex)->Name)}");
                Log($"SelfName:{ClientState.LocalPlayer?.Name}");
                Log($"isLeader:{GroupManager.Instance()->MainGroup.PartyLeaderIndex == 0}");
            }
            else if (args == "toggle")
            {
                Config.Enable = !Config.Enable;
                if (Config.Enable)
                {
                    ToastGui.ShowQuest(string.Format(localizer.Localize("Auto invite is turned on for \"{0}\""), Config.TextPattern),
                        new QuestToastOptions
                        {
                            DisplayCheckmark = true,
                            PlaySound = true
                        });
                }
                else
                {
                    ToastGui.ShowQuest(localizer.Localize("Auto invite is turned off"),
                        new QuestToastOptions
                        {
                            DisplayCheckmark = true,
                            PlaySound = true
                        });
                }
                Config.Save();
            }
            else if (timedRecruitment.TryProcessCommandTimedEnable(args))
            {
                //success
            }
            else if (CmdManager.Commands.TryGetValue("/xinvite", out var cmdInfo))
            {
                ChatGui.Print(cmdInfo.HelpMessage);
            }
        }

        private void InitUi()
        {
            uiModule = GameGui.GetUIModule();
            if (uiModule == IntPtr.Zero)
                throw new ApplicationException("uiModule was null");
            IntPtr step2 = Marshal.ReadIntPtr(uiModule) + 280;
            PluginLog.Info($"step2:0x{step2:X}");
            if (step2 == IntPtr.Zero)
                throw new ApplicationException("step2 was null");
            IntPtr step3 = Marshal.ReadIntPtr(step2);
            PluginLog.Info($"step3:0x{step3:X}");
            if (step3 == IntPtr.Zero)
                throw new ApplicationException("step3 was null");
            IntPtr step4 = Marshal.GetDelegateForFunctionPointer<GetMagicUIDelegate>(step3)(uiModule) + 6536;
            PluginLog.Info($"step4:0x{step4:X}");
            if (step4 == (IntPtr.Zero + 6536))
                throw new ApplicationException("step4 was null");
            uiInvite = Marshal.ReadInt64(step4);
            if (uiInvite == 0)
                throw new ApplicationException("uiInvite was 0");
        }

        public void Log(string message)
        {
            if (!Config.PrintMessage)
                return;
            var msg = $"[Inviter] {message}";
            PluginLog.Info(msg);
            ChatGui.Print(msg);
        }

        public void LogError(string message)
        {
            if (!Config.PrintError)
                return;
            var msg = $"[Inviter] {message}";
            PluginLog.Error(msg);
            ChatGui.PrintError(msg);
        }

        public static string ConvertSpanToString(Span<byte> byteSpan)
        {
            int length = 0;

            for (int i = 0; i < byteSpan.Length; i++)
            {
                if (byteSpan[i] == 0)
                {
                    break;
                }
                length++;
            }

            return Encoding.UTF8.GetString(byteSpan[..length]);
        }

        public static string StringFromNativeUtf8(IntPtr nativeUtf8)
        {
            int len = 0;
            while (Marshal.ReadByte(nativeUtf8, len) != 0)
                ++len;
            byte[] buffer = new byte[len];
            Marshal.Copy(nativeUtf8, buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer);
        }

        private unsafe void Chat_OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
        {
            if (!Config.Enable)
                return;
            if (Config.FilteredChannels.Contains(type))
                return;
            if (Config.HiddenChatType.Contains(type))
                return;
            if (Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51])
                return;
            bool matched = false;
            if (!Config.RegexMatch)
            {
                matched = message.TextValue.Contains(Config.TextPattern, StringComparison.CurrentCulture);
            }
            else
            {
                Regex rx = new(Config.TextPattern, RegexOptions.IgnoreCase);
                matched = rx.Matches(message.TextValue).Count > 0;
            }
            if (matched)
            {
                var senderPayload = sender.Payloads.Where(payload => payload is PlayerPayload).FirstOrDefault();
                if (senderPayload != null && senderPayload is PlayerPayload player)
                {
                    if (GroupManager.Instance()->MainGroup.MemberCount >= 8)
                    {
                        Log($"Full party, won't invite.");
                        if (timedRecruitment.isRunning)
                            timedRecruitment.FinishTimer();
                        return;
                    }
                    else
                    {
                        if (GroupManager.Instance()->MainGroup.MemberCount > 0)
                        {
                            var leader = GroupManager.Instance()->MainGroup.GetPartyMemberByIndex((int)GroupManager.Instance()->MainGroup.PartyLeaderIndex);
                            if (ClientState.LocalPlayer?.Name.TextValue != leader->NameString)
                            {
                                Log($"Not leader, won't invite. (Leader: {leader->NameString})");
                                return;
                            }
                        }
                        Log($"Party Count:{GroupManager.Instance()->MainGroup.MemberCount}");
                    }
                    var tc64 = Environment.TickCount64;
                    if (tc64 > NextInviteAt)
                    {
                        if (timedRecruitment.isRunning && timedRecruitment.MaxInvitations > 0)
                        {
                            if (timedRecruitment.InvitationAttempts >= timedRecruitment.MaxInvitations)
                            {
                                Log($"Reached target amound of invitations, won't invite {timedRecruitment.InvitationAttempts}/{timedRecruitment.MaxInvitations}");
                                timedRecruitment.FinishTimer();
                                return;
                            }
                            else
                            {
                                timedRecruitment.InvitationAttempts++;
                                Log($"Invitation {timedRecruitment.InvitationAttempts} out of {timedRecruitment.MaxInvitations}");
                            }
                        }
                        NextInviteAt = tc64 + Config.Ratelimit;
                        if (eureka_territories.Contains(GameMain.Instance()->CurrentTerritoryTypeId))
                            Task.Run(() => InviteByCID(player));
                        else
                            Task.Run(() => InviteByName(player));
                    }
                    else
                    {
                        Log($"Rate limiting invitation (next invite in {NextInviteAt - tc64} ms)");
                    }
                }
            }
        }

        public void InviteByName(PlayerPayload player)
        {
            int delay = Math.Max(0, Config.Delay);
            Thread.Sleep(delay);
            Log($"Invite:{player.PlayerName}@{player.World.Value.Name}");
            string player_name = player.PlayerName;
            var player_bytes = Encoding.UTF8.GetBytes(player_name);
            IntPtr mem1 = Marshal.AllocHGlobal(player_bytes.Length + 1);
            Marshal.Copy(player_bytes, 0, mem1, player_bytes.Length);
            Marshal.WriteByte(mem1, player_bytes.Length, 0);
            lock (LockInviteObj)
            {
                _EasierProcessInvite(uiInvite, 0, (short)player.World.RowId, mem1, (char)1);
            }
            Marshal.FreeHGlobal(mem1);
        }

        public void InviteByCID(PlayerPayload player)
        {
            int delay = Math.Max(500, Config.Delay); // 500ms to make sure the name2CID is updated
            Thread.Sleep(delay);
            string playerNameKey = $"{player.PlayerName}@{player.World.RowId}";
            if (!name2CID.TryGetValue(playerNameKey, out long CID))
            {
                LogError($"Unable to get CID:{player.PlayerName}@{player.World.Value.Name}");
                return;
            }
            Log($"Invite in Eureka:{player.PlayerName}@{player.World.Value.Name}");
            lock (LockInviteObj)
            {
                _EasierProcessEurekaInvite(uiInvite, CID);
            }
        }

        public char EasierProcessCIDDetour(nint a1, nint a2)
        {
            var ret = easierProcessCIDHook.Original(a1, a2);

            var CID = Marshal.ReadInt64(a2, 8);
            var world_id = Marshal.ReadInt16(a2, 20);
            var world = Data.GetExcelSheet<World>().GetRow((uint)world_id);
            var name = StringFromNativeUtf8(a2 + 24);
            Log($"{name}@{world.Name}:{CID}");

            var playerNameKey = $"{name}@{world_id}";
            name2CID.TryAdd(playerNameKey, CID);

            return ret;
        }
    }
}
