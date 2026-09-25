using SshManager.Core.Inventory;
using SshManager.Core.Models;

namespace SshManager.Tests;

public class CronTests
{
    [Fact]
    public void Crontab_Lines_Are_Parsed_And_Noise_Skipped()
    {
        var j = CronCollector.ParseLine("*/5  *  * * 1-5   /usr/bin/backup.sh --all  >> /var/log/b.log 2>&1", withUser: false)!;
        Assert.Equal("*/5 * * * 1-5", j.Schedule);
        Assert.Equal("/usr/bin/backup.sh --all  >> /var/log/b.log 2>&1", j.Command); // the command keeps its spacing
        Assert.Equal("@reboot", CronCollector.ParseLine("@reboot sleep 30 && docker start x", false)!.Schedule);

        var withUser = CronCollector.ParseLine("17 * * * * root cd / && run-parts --report /etc/cron.hourly", withUser: true)!;
        Assert.Equal("root", withUser.User);
        Assert.Equal("cd / && run-parts --report /etc/cron.hourly", withUser.Command);

        Assert.Null(CronCollector.ParseLine("# m h dom mon dow command", false));
        Assert.Null(CronCollector.ParseLine("SHELL=/bin/sh", false));
        Assert.Null(CronCollector.ParseLine("MAILTO = \"\"", false));
        Assert.Null(CronCollector.ParseLine("   ", false));
        Assert.Null(CronCollector.ParseLine("@sometimes run", false));
        Assert.Null(CronCollector.ParseLine("* * * * *", false)); // no command
        Assert.Null(CronCollector.ParseLine("* * * * * root", true)); // no command after the user
    }

    [Fact]
    public void Sections_Give_All_Kinds_Of_Jobs()
    {
        var sections = new Dictionary<string, string>
        {
            ["cron-users"] = "root|# DO NOT EDIT\nroot|0 3 * * * /opt/backup.sh\nbob|@hourly curl -s https://x.example/ping\n",
            ["cron-files"] = "/etc/crontab|SHELL=/bin/sh\n/etc/crontab|25 6 * * * root test -x /usr/sbin/anacron || run-parts /etc/cron.daily\n" +
                             "/etc/cron.d/certbot|0 */12 * * * root certbot -q renew\n",
            ["cron-periodic"] = "daily|/etc/cron.daily/logrotate\nweekly|/etc/cron.weekly/man-db\n",
            ["timers"] = """
                Id=apt-daily.timer
                Unit=apt-daily.service
                Description=Daily apt download activities
                ActiveState=active
                TimersCalendar={ OnCalendar=*-*-* 06,18:00:00 ; next_elapse=Thu 2026-09-25 18:23:41 UTC }
                TimersMonotonic=
                NextElapseUSecRealtime=Thu 2026-09-25 18:23:41 UTC
                LastTriggerUSec=Thu 2026-09-25 06:11:02 UTC

                Id=fstrim.timer
                Unit=fstrim.service
                Description=Discard unused blocks once a week
                ActiveState=inactive
                TimersCalendar={ OnCalendar=weekly ; next_elapse=n/a }
                TimersMonotonic={ OnBootSec=15min ; next_elapse=0 }
                NextElapseUSecRealtime=n/a
                LastTriggerUSec=n/a
                """,
        };
        Assert.True(CronCollector.Collected(sections));
        var jobs = CronCollector.Parse(sections);

        var root = Assert.Single(jobs, j => j.Kind == CronKind.Crontab && j.User == "root");
        Assert.Equal("/opt/backup.sh", root.Command);
        Assert.Equal("crontab", root.Source);
        Assert.Equal("@hourly", jobs.Single(j => j.User == "bob").Schedule);

        var certbot = jobs.Single(j => j.Source == "/etc/cron.d/certbot");
        Assert.Equal(CronKind.File, certbot.Kind);
        Assert.Equal("root", certbot.User);
        Assert.Equal("0 */12 * * * root certbot -q renew", certbot.Line);
        Assert.Single(jobs, j => j.Source == "/etc/crontab");

        var logrotate = jobs.Single(j => j.Command == "/etc/cron.daily/logrotate");
        Assert.Equal((CronKind.Periodic, "@daily", "/etc/cron.daily"), (logrotate.Kind, logrotate.Schedule, logrotate.Source));

        var apt = jobs.Single(j => j.Source == "apt-daily.timer");
        Assert.Equal("OnCalendar=*-*-* 06,18:00:00", apt.Schedule);
        Assert.Equal("apt-daily.service", apt.Command);
        Assert.Equal("Thu 2026-09-25 18:23:41 UTC", apt.Next);
        Assert.True(apt.Active);
        var fstrim = jobs.Single(j => j.Source == "fstrim.timer");
        Assert.Equal("OnCalendar=weekly; OnBootSec=15min", fstrim.Schedule);
        Assert.Null(fstrim.Next);
        Assert.False(fstrim.Active);

        Assert.Equal(8, jobs.Count); // 2 crontab, 2 files, 2 periodic, 2 timers
    }

    [Fact]
    public void Inventory_Keeps_Old_Jobs_When_Cron_Was_Not_Collected()
    {
        var facts = new ServerFacts { CronJobs = [new CronJob { Command = "old" }] };
        ServerInventoryService.Apply(facts, new Dictionary<string, string> { ["os"] = "ID=debian\n", ["end"] = "" });
        Assert.Equal("old", Assert.Single(facts.CronJobs).Command);
        ServerInventoryService.Apply(facts, new Dictionary<string, string> { ["cron-files"] = "", ["cron-periodic"] = "", ["end"] = "" });
        Assert.Empty(facts.CronJobs);
    }
}
