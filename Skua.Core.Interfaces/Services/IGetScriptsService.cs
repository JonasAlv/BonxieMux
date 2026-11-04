using Skua.Core.Models.GitHub;
using Skua.Core.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Skua.Core.Interfaces;

public interface IGetScriptsService : INotifyPropertyChanged
{
    int Downloaded => Scripts.Count(s => s.Downloaded);
    int Outdated => Scripts.Count(s => s.Outdated);
    int Total => Scripts.Count;
    int Missing => Total - Downloaded;
    RangedObservableCollection<ScriptInfo> Scripts { get; }

    ValueTask<List<ScriptInfo>> GetScriptsAsync(IProgress<string>? progress, CancellationToken token);

    Task RefreshScriptsAsync(IProgress<string>? progress, CancellationToken token);

    Task UpdateScriptDatesAsync(IProgress<string>? progress, CancellationToken token);

    Task<int> IncrementalUpdateScriptsAsync(IProgress<string>? progress, CancellationToken token);

    Task<long> CheckAdvanceSkillSetsUpdates();

    Task DownloadScriptAsync(ScriptInfo info, CancellationToken token = default);

    Task<int> DownloadAllWhereAsync(Func<ScriptInfo, bool> pred, IProgress<string>? progress = null, CancellationToken token = default);

    Task DeleteScriptAsync(ScriptInfo info);

    Task<bool> UpdateSkillSetsFile();

    Task<bool> UpdateQuestDataFile();

    Task<long> CheckJunkItemsUpdates();

    Task<bool> UpdateJunkItemsFile();
}
