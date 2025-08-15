using Dalamud.Game.Gui.Toast;
using System;
using System.Threading;

namespace Inviter
{
    class TimedEnable
    {
        internal long runUntil = 0;
        internal long nextNotification = 0;
        internal volatile bool isRunning = false;
        internal uint MaxInvitations = 0;
        internal uint InvitationAttempts = 0;

        internal TimedEnable() { }

        internal void Run()
        {
            isRunning = true;
            nextNotification = Environment.TickCount64 + (runUntil - Environment.TickCount64) / 2;
            try
            {
                Inviter.Plugin.Config.Enable = true;
                while (Environment.TickCount64 < runUntil)
                {
                    Thread.Sleep(1000);
                    if (!Inviter.Plugin.Config.Enable)
                    {
                        runUntil = 0;
                        break;
                    }
                    if (Environment.TickCount64 >= nextNotification && Environment.TickCount64 < runUntil)
                    {
                        Inviter.ToastGui.ShowQuest(string.Format(Inviter.Plugin.localizer.Localize("Automatic recruitment enabled, {0} minutes left"),
                            Math.Ceiling((runUntil - Environment.TickCount64) / 60d / 1000d)));
                        UpdateTimeNextNotification();
                    }
                }
                Inviter.ToastGui.ShowQuest(Inviter.Plugin.localizer.Localize("Automatic recruitment finished"),
                    new QuestToastOptions()
                    {
                        DisplayCheckmark = true,
                        PlaySound = true
                    });
                Inviter.Plugin.Config.Enable = false;
            }
            catch (Exception e)
            {
                Inviter.ChatGui.Print("Error: " + e.Message + "\n" + e.StackTrace);
            }
            isRunning = false;
        }

        internal void UpdateTimeNextNotification()
        {
            nextNotification = Environment.TickCount64 + Math.Max(60 * 1000, (runUntil - Environment.TickCount64) / 2);
        }

        internal bool TryProcessCommandTimedEnable(string args)
        {
            var argsArray = args.Split([" "], StringSplitOptions.RemoveEmptyEntries);
            if (argsArray.Length == 2 && uint.TryParse(argsArray[0], out var time) && uint.TryParse(argsArray[1], out var limit))
            {
                ProcessCommandTimedEnable(time, limit);
                return true;
            }
            else if (uint.TryParse(argsArray[0], out time))
            {
                ProcessCommandTimedEnable(time, 0);
                return true;
            }
            return false;
        }

        void ProcessCommandTimedEnable(uint timeInMinutes, uint limit)
        {
            if (Inviter.Plugin.Config.Enable && !isRunning)
            {
                Inviter.ToastGui.ShowError(Inviter.Plugin.localizer.Localize("Can't start timed recruitment because Inviter is turned on permanently"));
            }
            else
            {
                try
                {
                    var time = timeInMinutes;
                    MaxInvitations = limit;
                    InvitationAttempts = 0;
                    if (time > 0)
                    {
                        runUntil = Environment.TickCount64 + time * 60 * 1000;
                        if (isRunning)
                        {
                            UpdateTimeNextNotification();
                        }
                        else
                        {
                            new Thread(new ThreadStart(Run)).Start();
                        }
                        Inviter.ToastGui.ShowQuest(string.Format(Inviter.Plugin.localizer.Localize("Commenced automatic recruitment for {0} minutes"), time),
                            new QuestToastOptions()
                            {
                                DisplayCheckmark = true,
                                PlaySound = true
                            });
                        if (limit > 0)
                        {
                            Inviter.ToastGui.ShowQuest(string.Format(Inviter.Plugin.localizer.Localize("Recruitment will finish after {0} invitation attempts"), limit),
                                new QuestToastOptions()
                                {
                                    DisplayCheckmark = false,
                                    PlaySound = false
                                });
                        }
                    }
                    else if (time == 0)
                    {
                        if (isRunning)
                        {
                            runUntil = 0;
                        }
                        else
                        {
                            Inviter.ToastGui.ShowError(Inviter.Plugin.localizer.Localize("Recruitment is not running, can not cancel"));
                        }
                    }
                    else
                    {
                        Inviter.ToastGui.ShowError(Inviter.Plugin.localizer.Localize("Time can not be negative"));
                    }
                }
                catch (Exception e)
                {
                    Inviter.ToastGui.ShowError(Inviter.Plugin.localizer.Localize("Please enter amount of time in minutes"));
                }
            }
        }
    }
}
