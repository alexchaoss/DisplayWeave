using System;
using System.IO;

namespace DisplayWeave
{
    public static partial class RuleTests
    {
        // Changing saved rules and layouts: a rule of an app that is not running, regions split or merged, and a failed profile switch.
        private static void EditingTests(Action<string, Action> test)
        {
            test("A saved rule changes without its app running", delegate
            {
                InDirectory("DisplayWeave-change-", delegate(string directory)
                {
                    using (RegionService service = new RegionService(directory))
                    {
                        ApplicationRule rule = Rule(@"C:\Apps\NotRunning.exe", "GameWindow", ApplicationAction.MaximizeInRegion);
                        rule.MonitorDevice = service.Layout.MonitorDevice; rule.ZoneId = service.Layout.Zones[0].Id; rule.ProfileName = "Default";
                        service.Rules.Put(rule);
                        service.Save();
                        Assert(service.ChangeRule(rule, 1, ApplicationAction.BorderlessInRegion).StartsWith("Saved for"), "The rule was not changed.");
                        ApplicationRule changed = service.Rules.Rules[0];
                        Assert(service.Rules.Rules.Count == 1 && changed.Action == ApplicationAction.BorderlessInRegion &&
                            changed.ZoneId == service.Layout.Zones[1].Id && changed.ProfileName == "Default" && changed.ExecutablePath == rule.ExecutablePath,
                            "The changed rule lost its app, region or profile.");
                        Assert(!service.ChangeRule(changed, -1, ApplicationAction.BorderlessInRegion).StartsWith("Saved for") &&
                            !service.ChangeRule(changed, 9, ApplicationAction.ForceFullscreen).StartsWith("Saved for") &&
                            service.Rules.Rules[0] == changed, "A rule was changed without the region it needs.");
                        service.ChangeRule(changed, -1, ApplicationAction.ForceFullscreen);
                        Assert(service.Rules.Rules.Count == 1 && service.Rules.Rules[0].ZoneId == null, "An unpinned Force rule kept a region.");
                        service.ChangeRule(service.Rules.Rules[0], 0, ApplicationAction.Ignore);
                        Assert(service.Rules.Rules.Count == 1 && service.Rules.Rules[0].ProfileName == null && service.Rules.Rules[0].ZoneId == null,
                            "An Ignore rule did not apply in every profile.");
                    }
                    string warning;
                    ApplicationRules saved = ApplicationRules.Load(Path.Combine(directory, "rules.xml"), out warning);
                    Assert(saved.Rules.Count == 1 && saved.Rules[0].Action == ApplicationAction.Ignore, "The changed rule was not saved.");
                });
            });
            test("Saving a split or merged region unpins its Force and Maximize rules only", delegate
            {
                InDirectory("DisplayWeave-unpin-", delegate(string directory)
                {
                    using (RegionService service = new RegionService(directory))
                    {
                        string split = service.Layout.Zones[1].Id;
                        Func<ApplicationAction, string, string, ApplicationRule> add = delegate(ApplicationAction action, string zone, string profile)
                        {
                            ApplicationRule rule = Rule(@"C:\Apps\" + action + profile + ".exe", "Window", action);
                            rule.MonitorDevice = service.Layout.MonitorDevice; rule.ZoneId = zone; rule.ProfileName = profile;
                            service.Rules.Rules.Add(rule);
                            return rule;
                        };
                        ApplicationRule force = add(ApplicationAction.ForceFullscreen, split, "Default");
                        ApplicationRule maximize = add(ApplicationAction.MaximizeInRegion, split, "Default");
                        ApplicationRule borderless = add(ApplicationAction.BorderlessInRegion, split, "Default");
                        ApplicationRule kept = add(ApplicationAction.MaximizeInRegion, service.Layout.Zones[0].Id, "Default");
                        ApplicationRule other = add(ApplicationAction.ForceFullscreen, split, "Gaming");
                        Assert(service.SaveLayout() == null && force.ZoneId == split, "A rule was unpinned while its region still existed.");
                        service.Layout.Zones[1].Id = Guid.NewGuid().ToString("N"); // as splitting or merging does
                        string note = service.SaveLayout();
                        Assert(note != null && note.StartsWith("2 app rules") && note.Contains("1 Borderless"), "The unpinned rules were not reported: " + note);
                        Assert(force.ZoneId == null && maximize.ZoneId == null, "A Force or Maximize rule kept its missing region.");
                        Assert(borderless.ZoneId == split && kept.ZoneId == service.Layout.Zones[0].Id && other.ZoneId == split,
                            "A Borderless rule, a rule of a kept region or another profile's rule was changed.");
                        Assert(service.SaveLayout() == null, "Rules were reported again.");
                    }
                    string warning;
                    ApplicationRules saved = ApplicationRules.Load(Path.Combine(directory, "rules.xml"), out warning);
                    Assert(saved.Rules.FindAll(delegate(ApplicationRule r) { return r.ZoneId == null; }).Count == 2, "The unpinned rules were not saved.");
                });
            });
            test("A failed profile switch keeps the profile and handling as they were", delegate
            {
                InDirectory("DisplayWeave-switch-", delegate(string directory)
                {
                    using (RegionService service = new RegionService(directory))
                    {
                        service.CreateProfile("Gaming");
                        service.SwitchProfile("Default");
                        // layout.xml becomes a folder, so saving the switched layout fails.
                        string layout = Path.Combine(directory, "layout.xml");
                        File.Delete(layout);
                        Directory.CreateDirectory(layout);
                        foreach (bool paused in new[] { false, true })
                        {
                            if (paused) service.Engine.Pause();
                            bool failed = false;
                            try { service.SwitchProfile("Gaming"); }
                            catch (IOException) { failed = true; }
                            Assert(failed && service.Layout.ProfileName == "Default" && service.Engine.Paused == paused,
                                "After a failed switch the profile is " + service.Layout.ProfileName + " and handling paused is " + service.Engine.Paused + ".");
                        }
                    }
                });
            });
        }

        private static void InDirectory(string prefix, Action<string> body)
        {
            string directory = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { body(directory); }
            finally { Directory.Delete(directory, true); }
        }
    }
}
