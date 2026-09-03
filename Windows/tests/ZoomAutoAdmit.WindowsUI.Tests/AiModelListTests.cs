using System.IO;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public sealed class AiModelListTests
{
    private sealed class MemoryCatalog : IAiModelCatalog
    {
        public Dictionary<AiProvider, List<string>> Saved { get; } = new();
        public bool FailSave { get; init; }
        public IReadOnlyList<string> Read(AiProvider provider) =>
            Saved.TryGetValue(provider, out var models) ? models.ToArray() : [];
        public void Save(AiProvider provider, IReadOnlyList<string> models)
        {
            if (FailSave) throw new IOException("synthetic-disk-failure");
            Saved[provider] = [.. models];
        }
    }

    private static AiMatchingViewModel Create(IAiModelCatalog catalog) =>
        new(new AiSetupTests.MemoryStore(), new AiSetupTests.FakeAi(), catalog);

    [Fact]
    public void BuiltInListsOfferBothMiniModelsPerProvider()
    {
        Assert.Equal(new[] { "gpt-5.4-mini", "gpt-4.1-mini", "gpt-4.1" }, AiMatchingViewModel.BuiltInModels(AiProvider.OpenAI));
        Assert.Equal(new[] { "openai/gpt-4.1-mini", "openai/gpt-5.4-mini", "openai/gpt-4.1" },
            AiMatchingViewModel.BuiltInModels(AiProvider.OpenRouter));
    }

    [Fact]
    public void AddedModelIsSavedPerProviderAndReappearsInAFreshViewModel()
    {
        var catalog = new MemoryCatalog();
        using var vm = Create(catalog);
        vm.Model = "gpt-4.1-nano";
        vm.AddModelCommand.Execute(null);

        Assert.Contains("gpt-4.1-nano", vm.Models);
        Assert.True(vm.CanRemoveModel);
        Assert.Equal(new[] { "gpt-4.1-nano" }, catalog.Saved[AiProvider.OpenAI]);
        // The added ID belongs to the provider it was added under, not to every provider.
        vm.Provider = AiProvider.OpenRouter;
        Assert.DoesNotContain("gpt-4.1-nano", vm.Models);

        using var next = Create(catalog);
        Assert.Contains("gpt-4.1-nano", next.Models);
        Assert.Equal(AiMatchingViewModel.BuiltInModels(AiProvider.OpenAI).Count + 1, next.Models.Count);
    }

    [Theory]
    [InlineData("", "Type a model ID")]
    [InlineData("gpt 4", "Type a model ID")]
    [InlineData("gpt-4.1-mini", "already in")]
    public void InvalidOrDuplicateModelIsNeverAdded(string model, string expected)
    {
        var catalog = new MemoryCatalog();
        using var vm = Create(catalog);
        vm.Model = model;
        vm.AddModelCommand.Execute(null);

        Assert.Empty(catalog.Saved);
        Assert.Contains(expected, vm.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AiMatchingViewModel.BuiltInModels(AiProvider.OpenAI).Count, vm.Models.Count);
    }

    [Fact]
    public void OpenRouterStillRequiresAFullModelIdWhenAddingOne()
    {
        var catalog = new MemoryCatalog();
        using var vm = Create(catalog);
        vm.Provider = AiProvider.OpenRouter;
        vm.Model = "gpt-5.4-mini";
        vm.AddModelCommand.Execute(null);
        Assert.Empty(catalog.Saved);
        Assert.Contains("full model ID", vm.Status);

        vm.Model = "anthropic/claude-sonnet-4";
        vm.AddModelCommand.Execute(null);
        Assert.Equal(new[] { "anthropic/claude-sonnet-4" }, catalog.Saved[AiProvider.OpenRouter]);
        Assert.Contains("anthropic/claude-sonnet-4", vm.Models);
    }

    [Fact]
    public void OnlyAddedModelsCanBeRemovedAndRemovalIsPersisted()
    {
        var catalog = new MemoryCatalog();
        using var vm = Create(catalog);
        vm.Model = "gpt-4.1-nano";
        vm.AddModelCommand.Execute(null);

        vm.Model = "gpt-4.1-mini"; // Built-in.
        Assert.False(vm.CanRemoveModel);
        vm.RemoveModelCommand.Execute(null);
        Assert.Contains("added yourself", vm.Status);
        Assert.Equal(new[] { "gpt-4.1-nano" }, catalog.Saved[AiProvider.OpenAI]);

        vm.Model = "gpt-4.1-nano";
        vm.RemoveModelCommand.Execute(null);
        Assert.DoesNotContain("gpt-4.1-nano", vm.Models);
        Assert.Empty(catalog.Saved[AiProvider.OpenAI]);
        Assert.Equal(AiMatchingViewModel.BuiltInModels(AiProvider.OpenAI)[0], vm.Model);
    }

    [Fact]
    public void ADiskFailureLeavesTheListUnchangedAndSaysSo()
    {
        using var vm = Create(new MemoryCatalog { FailSave = true });
        vm.Model = "gpt-4.1-nano";
        vm.AddModelCommand.Execute(null);

        Assert.DoesNotContain("gpt-4.1-nano", vm.Models);
        Assert.Contains("Cannot save the model list", vm.Status);
        Assert.DoesNotContain("synthetic-disk-failure", vm.Status);
    }

    [Fact]
    public void CatalogFileRoundTripsPerProviderAndRejectsAForeignFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "zoom-auto-admit-tests", Guid.NewGuid().ToString("N"), "models.json");
        try
        {
            var catalog = new AiModelCatalog(path);
            Assert.Empty(catalog.Read(AiProvider.OpenAI));
            catalog.Save(AiProvider.OpenAI, ["gpt-4.1-nano", "gpt-4.1-nano", "bad id", ""]);
            catalog.Save(AiProvider.OpenRouter, ["openai/gpt-5.4-mini"]);

            Assert.Equal(new[] { "gpt-4.1-nano" }, new AiModelCatalog(path).Read(AiProvider.OpenAI));
            Assert.Equal(new[] { "openai/gpt-5.4-mini" }, new AiModelCatalog(path).Read(AiProvider.OpenRouter));

            File.WriteAllText(path, "{\"schemaVersion\":99,\"models\":{}}");
            Assert.Throws<InvalidDataException>(() => new AiModelCatalog(path).Read(AiProvider.OpenAI));
        }
        finally
        {
            string directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
