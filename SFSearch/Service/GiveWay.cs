namespace SFSearch.Service
{
    /// <summary>
    /// The coordinator's rule for the indexer's jobs that don't give way (ServiceHost's watchdog calls it once a second).
    /// When an agent has to wait and no worker is free, the job running in the indexer lane is asked to give way. A job
    /// that listens saves its work and returns at once: the coordinator takes it off its worker and queues it again, its
    /// <c>Asked</c> cleared. A job that hasn't returned 2 s after it was asked, while an agent still waits, is stopped and
    /// queued again. Agents' jobs are never stopped by this rule; neither is a job that is ending already (cancelled, or
    /// stopped for another reason). Plain code, no service: SFSearch.ServiceTest `giveway` tests it.
    /// </summary>
    internal static class GiveWay
    {
        /// <summary>The indexer's lane: the header index, the link index and the start's full check, at low priority.</summary>
        public const string IndexLane = "~index";

        /// <summary>How long a job asked to give way has to do so.</summary>
        public static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

        /// <summary>A job on a worker, as the rule sees it: its lane, when it was asked to give way (null: not asked), and whether it is ending already.</summary>
        public readonly record struct Running(string Lane, DateTime? Asked, bool Ending);

        /// <summary>When the job is stopped if it is still running then and an agent still waits; null when this rule never stops it.</summary>
        public static DateTime? StopAt(Running job) =>
            job.Lane == IndexLane && job.Asked is { } asked && !job.Ending ? asked + Grace : null;

        /// <summary>Which of the running jobs to stop now (their places in the list).</summary>
        public static List<int> ToStop(IReadOnlyList<Running> jobs, DateTime now, bool agentsWaiting)
        {
            var stop = new List<int>();
            if (!agentsWaiting) return stop;
            for (int i = 0; i < jobs.Count; i++)
                if (StopAt(jobs[i]) is { } at && now > at) stop.Add(i);
            return stop;
        }
    }
}
