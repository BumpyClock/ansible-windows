using Ansible.Core;

namespace Ansible.Tests;

public sealed partial class DictationSessionTests
{
    [Fact]
    public async Task UnconfiguredDictationPrefersNemotronAndExcludesDiarizationCompanions()
    {
        var nemotron = Model with { Id = AudioModelDefaults.RecognitionModelId, Family = "nemotron_asr", Task = "asr" };
        var companion = Model with { Id = AudioModelDefaults.DiarizationModelId, Family = "nemotron_3_diar", Task = "diar" };
        var session = Create(new FakeEngine
        {
            Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>([companion, Model, nemotron])
        });
        try
        {
            await session.InitializeAsync();
            Assert.Equal(nemotron, session.State.SelectedModel);
            Assert.Equal(new[] { Model, nemotron }, session.State.Models);
        }
        finally { await session.CloseAsync(); }
    }

    [Fact]
    public async Task InstalledDiarizerAloneDoesNotBecomeADictationModel()
    {
        var companion = Model with { Id = AudioModelDefaults.DiarizationModelId, Family = "nemotron_3_diar", Task = "diar" };
        var session = Create(new FakeEngine
        {
            Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>([companion])
        });
        try
        {
            await session.InitializeAsync();
            Assert.Empty(session.State.Models);
            Assert.Null(session.State.SelectedModel);
            Assert.False(session.State.CanStart);
        }
        finally { await session.CloseAsync(); }
    }

    [Fact]
    public async Task NemotronDefaultDoesNotOverrideAnExplicitSavedModel()
    {
        var nemotron = Model with { Id = AudioModelDefaults.RecognitionModelId, Family = "nemotron_asr", Task = "asr" };
        var settings = new AppSettingsStore(SettingsPath);
        settings.Save(new() { ModelId = Model.Id });
        var session = Create(new FakeEngine
        {
            Connecting = _ => Task.FromResult<IReadOnlyList<AudioModel>>([nemotron, Model])
        }, settings: settings);
        try
        {
            await session.InitializeAsync();
            Assert.Equal(Model, session.State.SelectedModel);
            Assert.Equal(Model.Id, settings.Load().ModelId);
        }
        finally { await session.CloseAsync(); }
    }
}
