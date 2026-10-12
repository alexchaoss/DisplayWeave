using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace DisplayWeave
{
    public static partial class RuleTests
    {
        // Who a rule is for and where: stable window classes, Store apps, layout profiles, last-used dates and renamed displays.
        private static void IdentityTests(Action<string, Action> test)
        {
            test("Window classes that change on every run still match their rule", delegate
            {
                Assert(ApplicationRule.StableClass("HwndWrapper[Editor.exe;;3f2504e0-4f89-11d3-9a0c-0305e82c3301]") == "HwndWrapper[Editor.exe;;*]", "A WPF class kept its GUID.");
                Assert(ApplicationRule.StableClass("WindowsForms10.Window.8.app.0.141b42a_r6_ad1") == "WindowsForms10.Window.8.app.0.*", "A WinForms class kept its suffix.");
                Assert(ApplicationRule.StableClass("Afx:00400000:b:00010003:00000006:00000000") == "Afx:*", "An MFC class kept its addresses.");
                foreach (string plain in new[] { "Chrome_WidgetWin_1", "CabinetWClass", "UnityWndClass", "SDL_app" })
                    Assert(ApplicationRule.StableClass(plain) == plain, "An ordinary class changed: " + plain);
                ApplicationRule rule = Rule(@"C:\Apps\Editor.exe", "HwndWrapper[Editor.exe;;3f2504e0-4f89-11d3-9a0c-0305e82c3301]", ApplicationAction.MaximizeInRegion);
                Assert(rule.Matches(new ApplicationIdentity { ExecutablePath = @"c:\apps\editor.exe", WindowClass = "HwndWrapper[Editor.exe;;9b7e8a1c-0000-4d6e-8f00-1234567890ab]" }),
                    "The rule did not match the app's next run.");
                Assert(!rule.Matches(new ApplicationIdentity { ExecutablePath = @"C:\Apps\Editor.exe", WindowClass = "HwndWrapper[Other.exe;;9b7e8a1c-0000-4d6e-8f00-1234567890ab]" }),
                    "The rule matched another window class.");
            });
            test("Store apps are told apart by their app ID, and Ignore replaces an app's other rules", delegate
            {
                ApplicationRules rules = new ApplicationRules();
                ApplicationRule first = Rule(@"C:\Windows\System32\ApplicationFrameHost.exe", "ApplicationFrameWindow", ApplicationAction.MaximizeInRegion);
                first.AppId = "Example.Calculator_8wekyb3d8bbwe!App"; first.ProfileName = "Default";
                ApplicationRule second = Rule(first.ExecutablePath, first.WindowClass, ApplicationAction.BorderlessInRegion);
                second.AppId = "Example.Photos_8wekyb3d8bbwe!App"; second.ProfileName = "Default";
                rules.Put(first); rules.Put(second);
                Assert(rules.Rules.Count == 2, "One Store app's rule replaced another's.");
                Assert(first.Matches(new ApplicationIdentity { ExecutablePath = first.ExecutablePath, WindowClass = first.WindowClass, AppId = "example.calculator_8wekyb3d8bbwe!app" }) &&
                    !first.Matches(new ApplicationIdentity { ExecutablePath = first.ExecutablePath, WindowClass = first.WindowClass, AppId = second.AppId }) &&
                    !first.Matches(new ApplicationIdentity { ExecutablePath = first.ExecutablePath, WindowClass = first.WindowClass }), "A Store app rule matched the wrong app.");
                Assert(StoreAppIdentity.DisplayName(first.AppId) == "Example.Calculator", "The Store app's name is wrong.");
                ApplicationRule gaming = Rule(first.ExecutablePath, first.WindowClass, ApplicationAction.ForceFullscreen);
                gaming.AppId = first.AppId; gaming.ProfileName = "Gaming";
                rules.Put(gaming);
                Assert(rules.Rules.Count == 3, "A rule for another profile replaced the app's rule.");
                ApplicationRule ignore = Rule(first.ExecutablePath, first.WindowClass, ApplicationAction.Ignore);
                ignore.AppId = first.AppId;
                rules.Put(ignore);
                Assert(rules.Rules.Count == 2 && rules.Rules.Contains(second) && rules.Rules.Contains(ignore), "Ignore did not replace the app's rules in every profile.");
            });
            test("Rules follow the layout profile and record the day they were last used", delegate
            {
                using (RefusingWindow window = new RefusingWindow())
                {
                    DisplayMonitor primary = DisplayMonitor.FromRectangle(new Rectangle(0, 0, 1, 1));
                    LayoutSettings layout = new LayoutSettings { MonitorDevice = primary.DeviceName, ProfileName = "Gaming", AutomaticRegions = false };
                    ApplicationIdentity editor = new ApplicationIdentity { ExecutablePath = @"C:\Apps\Editor.exe", WindowClass = "EditorWindow",
                        ProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id, CreationTime = 1, Handle = window.Handle };
                    ApplicationRules rules = new ApplicationRules();
                    ApplicationRule work = Rule(editor.ExecutablePath, editor.WindowClass, ApplicationAction.MaximizeInRegion);
                    work.MonitorDevice = primary.DeviceName; work.ZoneId = layout.Zones[0].Id; work.ProfileName = "Work";
                    ApplicationRule gaming = Rule(editor.ExecutablePath, editor.WindowClass, ApplicationAction.MaximizeInRegion);
                    gaming.MonitorDevice = primary.DeviceName; gaming.ZoneId = layout.Zones[1].Id; gaming.ProfileName = "Gaming";
                    ApplicationRule every = Rule(editor.ExecutablePath, editor.WindowClass, ApplicationAction.MaximizeInRegion);
                    every.MonitorDevice = primary.DeviceName; every.ZoneId = layout.Zones[0].Id; every.ProfileName = "";
                    rules.Rules.AddRange(new[] { work, every, gaming });
                    Rectangle region = layout.Zones[1].ToPixels(primary.WorkingArea);
                    using (FullscreenController controller = new FullscreenController())
                    {
                        controller.Tick(new[] { new WindowCandidate { Handle = window.Handle, Title = "Editor", Identity = editor } }, rules, layout);
                        Assert(window.VisibleFrame == region, "The active profile's rule did not win: " + window.VisibleFrame + " instead of " + region + ".");
                        Assert(gaming.LastMatched.Date == DateTime.UtcNow.Date && work.LastMatched == DateTime.MinValue && controller.RulesUsed,
                            "The rule's last-used date was not recorded, or another profile's rule was.");
                        controller.RulesUsed = false;
                        controller.Tick(new[] { new WindowCandidate { Handle = window.Handle, Title = "Editor", Identity = editor } }, rules, layout);
                        Assert(!controller.RulesUsed, "Rules were marked for saving again on the same day.");
                    }
                }
            });
            test("Layout profiles copy, switch and delete with their own rules", delegate
            {
                string directory = Path.Combine(Path.GetTempPath(), "DisplayWeave-profiles-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    using (RegionService service = new RegionService(directory))
                    {
                        Assert(service.Profiles().Count == 1 && service.Layout.ProfileName == "Default", "A new install is not one Default profile.");
                        ApplicationRule rule = Rule(@"C:\Apps\Game.exe", "GameWindow", ApplicationAction.BorderlessInRegion);
                        rule.MonitorDevice = service.Layout.MonitorDevice; rule.ZoneId = service.Layout.Zones[0].Id; rule.ProfileName = "Default";
                        service.Rules.Put(rule);
                        service.Save();
                        Assert(!service.CreateProfile("../escape").StartsWith("Layout profile"), "A profile name with a path was accepted.");
                        Assert(service.CreateProfile("Gaming").StartsWith("Layout profile Gaming created"), "The profile was not created.");
                        Assert(service.Layout.ProfileName == "Gaming" && service.Rules.Rules.Count == 2 &&
                            service.Rules.Rules.Exists(delegate(ApplicationRule r) { return r.ProfileName == "Gaming" && r.ZoneId == rule.ZoneId; }),
                            "The new profile is not active or did not get a copy of the rules.");
                        service.Layout.Zones = LayoutSettings.Preset(1);
                        service.SaveLayout();
                        Assert(service.SwitchProfile("Default").StartsWith("Switched") && service.Layout.Zones.Count == 2 && service.Layout.ProfileName == "Default",
                            "Switching back did not bring the Default layout back.");
                        Assert(service.SwitchProfile("Gaming").StartsWith("Switched") && service.Layout.Zones.Count == 3, "The Gaming layout was not kept.");
                        Assert(!service.DeleteProfile("Gaming").EndsWith("deleted."), "The profile in use was deleted.");
                        service.SwitchProfile("Default");
                        Assert(service.DeleteProfile("Gaming").EndsWith("deleted.") && service.Profiles().Count == 1 &&
                            !service.Rules.Rules.Exists(delegate(ApplicationRule r) { return r.ProfileName == "Gaming"; }), "The profile or its rules were not deleted.");
                    }
                    using (RegionService reloaded = new RegionService(directory))
                        Assert(reloaded.Layout.ProfileName == "Default" && reloaded.Rules.Rules.Count == 1, "The active profile or rules did not survive a restart.");
                }
                finally { Directory.Delete(directory, true); }
            });
            test("A layout follows its monitor when Windows renames the display, but never to another size", delegate
            {
                string directory = Path.Combine(Path.GetTempPath(), "DisplayWeave-rebind-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    DisplayMonitor primary = DisplayMonitor.FromRectangle(new Rectangle(0, 0, 1, 1));
                    int sameSize = DisplayMonitor.All().FindAll(delegate(DisplayMonitor m) { return m.Bounds.Size == primary.Bounds.Size; }).Count;
                    using (RegionService service = new RegionService(directory))
                    {
                        ApplicationRule rule = Rule(@"C:\Apps\Game.exe", "GameWindow", ApplicationAction.MaximizeInRegion);
                        rule.MonitorDevice = @"\\.\DISPLAY99"; rule.ZoneId = service.Layout.Zones[0].Id;
                        service.Rules.Put(rule);
                        service.Layout.MonitorDevice = @"\\.\DISPLAY99";
                        service.Layout.MonitorId = "not a monitor";
                        service.Layout.MonitorWidth = primary.Bounds.Width + 1; service.Layout.MonitorHeight = primary.Bounds.Height;
                        Assert(service.RebindMonitor() == null && service.Layout.MonitorDevice == @"\\.\DISPLAY99", "The layout moved to a display of another size.");
                        service.Layout.MonitorWidth = primary.Bounds.Width;
                        string moved = service.RebindMonitor();
                        if (sameSize == 1)
                            Assert(moved != null && service.Layout.MonitorDevice == primary.DeviceName && rule.MonitorDevice == primary.DeviceName,
                                "The layout did not move to the only display of its size.");
                        else Assert(moved == null, "The layout picked one of several displays of its size.");
                        if (primary.DeviceId.Length > 0)
                        {
                            service.Layout.MonitorDevice = rule.MonitorDevice = @"\\.\DISPLAY99";
                            service.Layout.MonitorId = primary.DeviceId; service.Layout.MonitorWidth = 1;
                            Assert(service.RebindMonitor() != null && service.Layout.MonitorDevice == primary.DeviceName && rule.MonitorDevice == primary.DeviceName,
                                "The layout did not follow its monitor's device path.");
                        }
                    }
                }
                finally { Directory.Delete(directory, true); }
            });
        }
    }
}
