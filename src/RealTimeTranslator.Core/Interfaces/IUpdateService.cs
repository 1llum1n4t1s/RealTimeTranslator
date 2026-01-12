using System;
using System.Threading;
using System.Threading.Tasks;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Interfaces;

public interface IUpdateService
{
    event EventHandler<UpdateStatusChangedEventArgs>? StatusChanged;
    event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;
    event EventHandler<UpdateReadyEventArgs>? UpdateReady;

    void UpdateSettings(UpdateSettings settings);

    Task StartAsync(CancellationToken cancellationToken);

    Task CheckOnceAsync(CancellationToken cancellationToken);

    Task<bool> CheckAndApplyStartupAsync(CancellationToken cancellationToken);

    Task ApplyUpdateAsync(CancellationToken cancellationToken);

    void DismissPendingUpdate();
}
