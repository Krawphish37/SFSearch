using SFSearch.Service;

namespace SFSearch.ServiceTest;

/// <summary>
/// `giveway`: the coordinator's rule for an indexer job that doesn't give way (SFSearch\Service\GiveWay.cs, compiled in),
/// as plain code: no service, no home. The watchdog looks once a second; here the looks are given times.
///   1. a job in the indexer lane that doesn't answer is stopped after 2 s, at the first look past that, while an agent waits;
///   2. one that answers isn't stopped: the coordinator took it off its worker and queued it again with its ask cleared;
///   3. an agent's job is never stopped by this rule (asked or not), nor a job that is ending already;
///   4. several jobs at once: each by its own ask, and none once no agent waits.
/// (J50: this was a check of the running service until then, which needed the request field `lane`, now gone.)
/// </summary>
public static partial class T
{
    static void GiveWayRule()
    {
        const string Index = GiveWay.IndexLane;
        var t0 = new DateTime(2026, 11, 15, 10, 0, 0, 300);
        static string Ms(DateTime t, DateTime from) => $"{(t - from).TotalMilliseconds:0} ms";
        // The once-a-second looks of the watchdog, from the second after the ask: the first that stops the job
        static DateTime? FirstStop(GiveWay.Running job, DateTime from, int looks, Func<int, bool> agentsWaiting)
        {
            var look = from.Date.AddHours(from.Hour).AddMinutes(from.Minute).AddSeconds(from.Second + 1);
            for (int k = 0; k < looks; k++, look = look.AddSeconds(1))
                if (GiveWay.ToStop([job], look, agentsWaiting(k)).Count > 0) return look;
            return null;
        }

        // 1. A deaf indexer job: due 2 s after the ask, stopped at the first look after that
        var deaf = new GiveWay.Running(Index, t0, Ending: false);
        Check("giveway: a deaf indexer job is due 2 s after it was asked", GiveWay.StopAt(deaf) == t0.AddSeconds(2), $"asked {t0:HH:mm:ss.fff}, due {GiveWay.StopAt(deaf):HH:mm:ss.fff}");
        Check("giveway: not stopped before the 2 s are up", GiveWay.ToStop([deaf], t0.AddMilliseconds(1999), true).Count == 0 && GiveWay.ToStop([deaf], t0.AddSeconds(2), true).Count == 0,
            "at 1,999 and 2,000 ms after the ask");
        Check("giveway: stopped once the 2 s are up, while an agent waits", GiveWay.ToStop([deaf], t0.AddMilliseconds(2001), true) is [0], "at 2,001 ms after the ask");
        var stoppedAt = FirstStop(deaf, t0, 10, _ => true);
        Check("giveway: with a look once a second, a deaf job is stopped 2 to 3 s after the ask", stoppedAt is { } s1 && s1 - t0 > GiveWay.Grace && s1 - t0 <= GiveWay.Grace + TimeSpan.FromSeconds(1),
            stoppedAt is { } s ? $"asked {t0:HH:mm:ss.fff}, stopped at the look of {s:HH:mm:ss.fff} ({Ms(s, t0)} later)" : "never stopped");
        Check("giveway: not stopped while no agent waits; stopped at the next look once one does", FirstStop(deaf, t0, 10, k => k >= 5) == t0.Date.AddHours(10).AddSeconds(6),
            $"agents waiting from the 6th look on: stopped at {FirstStop(deaf, t0, 10, k => k >= 5):HH:mm:ss.fff}");

        // 2. A job that answers: it returned 0.4 s after the ask; the coordinator queued it again, its ask cleared. Run
        // again later while agents wait, it isn't stopped (until it is asked again)
        var again = new GiveWay.Running(Index, null, Ending: false);
        Check("giveway: a job that answered isn't stopped", GiveWay.StopAt(again) == null && FirstStop(again, t0.AddSeconds(5), 30, _ => true) == null,
            "queued again with its ask cleared, then running 30 s with agents waiting: never stopped");
        var askedAgain = again with { Asked = t0.AddSeconds(20) };
        Check("giveway: asked again, it has its own 2 s again", GiveWay.StopAt(askedAgain) == t0.AddSeconds(22), $"due {GiveWay.StopAt(askedAgain):HH:mm:ss.fff}");

        // 3. Agents' jobs, and jobs ending already
        var agent = new GiveWay.Running("cli", t0, Ending: false);
        var agentNamed = new GiveWay.Running("b1", null, Ending: false);
        Check("giveway: an agent's job is never stopped by this rule", GiveWay.StopAt(agent) == null && GiveWay.StopAt(agentNamed) == null && FirstStop(agent, t0, 60, _ => true) == null,
            "lane cli with an ask 60 s old, lane b1 without one");
        var ending = new GiveWay.Running(Index, t0, Ending: true);
        Check("giveway: an indexer job that is ending already (cancelled, or stopped for another reason) isn't stopped again", GiveWay.StopAt(ending) == null && FirstStop(ending, t0, 10, _ => true) == null);
        var notAsked = new GiveWay.Running(Index, null, Ending: false);
        Check("giveway: an indexer job not asked to give way isn't stopped", FirstStop(notAsked, t0, 60, _ => true) == null);

        // 4. Several at once: two deaf indexer jobs asked 1.5 s apart, an agent's job, an ending one, one not asked
        GiveWay.Running[] jobs =
        [
            new(Index, t0, false),
            new("cli", t0, false),
            new(Index, t0.AddMilliseconds(1500), false),
            new(Index, t0, true),
            new(Index, null, false),
            new("a2", null, false),
        ];
        string Stops(DateTime at, bool agents) => string.Join(",", GiveWay.ToStop(jobs, at, agents));
        var at1 = t0.AddMilliseconds(2500); var at2 = t0.AddMilliseconds(3600);
        Check("giveway: several jobs at once: each by its own ask", Stops(t0.AddMilliseconds(1000), true) == "" && Stops(at1, true) == "0" && Stops(at2, true) == "0,2",
            $"at 1.0 s: [{Stops(t0.AddMilliseconds(1000), true)}]; at 2.5 s: [{Stops(at1, true)}]; at 3.6 s: [{Stops(at2, true)}] (0 and 2 are the indexer's deaf jobs)");
        Check("giveway: several jobs at once: none once no agent waits", Stops(at2, false) == "", $"at 3.6 s with no agent waiting: [{Stops(at2, false)}]");
    }
}
