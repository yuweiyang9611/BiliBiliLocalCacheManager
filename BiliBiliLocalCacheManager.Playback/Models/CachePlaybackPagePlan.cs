using System.Collections.ObjectModel;

namespace BiliBiliLocalCacheManager.Playback.Models;

public sealed class CachePlaybackPagePlan
{
    private readonly ReadOnlyCollection<CachePlaybackPlan> _candidatePlans;

    public CachePlaybackPagePlan(
        long avid,
        string title,
        int pageIndex,
        string partName,
        IEnumerable<CachePlaybackPlan> candidatePlans,
        CachePlaybackPlan selectedPlan,
        string? message = null)
    {
        ArgumentNullException.ThrowIfNull(candidatePlans);
        ArgumentNullException.ThrowIfNull(selectedPlan);

        var planList = candidatePlans.ToList();
        if (planList.Count == 0)
        {
            throw new ArgumentException("Page plan must contain at least one candidate.", nameof(candidatePlans));
        }

        if (!planList.Contains(selectedPlan))
        {
            throw new ArgumentException("Selected plan must belong to the candidate collection.", nameof(selectedPlan));
        }

        Avid = avid;
        Title = title;
        PageIndex = pageIndex;
        PartName = partName;
        _candidatePlans = new ReadOnlyCollection<CachePlaybackPlan>(planList);
        SelectedPlan = selectedPlan;
        Message = message;
    }

    public long Avid { get; }

    public string Title { get; }

    public int PageIndex { get; }

    public string PartName { get; }

    public IReadOnlyList<CachePlaybackPlan> CandidatePlans => _candidatePlans;

    public CachePlaybackPlan SelectedPlan { get; }

    public string? Message { get; }

    public bool IsPlayable => SelectedPlan.IsPlayable;
}
