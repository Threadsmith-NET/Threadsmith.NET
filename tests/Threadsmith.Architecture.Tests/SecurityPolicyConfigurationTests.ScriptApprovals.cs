namespace Threadsmith.Architecture.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    /// <summary>Script ceilings accept literal filenames, not paths, patterns or command text.</summary>
    [Theory]
    [InlineData("deployed.ps1", true)]
    [InlineData("deployed.sh", true)]
    [InlineData("naïve's script.PS1", true)]
    [InlineData("fixture script.sh", true)]
    [InlineData("program name.exe", false)]
    [InlineData("*.ps1", false)]
    [InlineData("script?.sh", false)]
    [InlineData("/tmp/script.sh", false)]
    [InlineData("C:\\scripts\\script.ps1", false)]
    [InlineData(".\\script.ps1", false)]
    [InlineData("my\nscript.ps1", false)]
    [InlineData("my\tscript.sh", false)]
    public void ScriptExecutableCeilingsAcceptOnlyLiteralBasenames(string name, bool allowed)
    {
        var document = JsonSerializer.Serialize(new { tools = new { allowedExecutables = new[] { name } } });
        File.WriteAllText(_paths.UserConfiguration, document);

        if (allowed)
        {
            Assert.Equal(name, Assert.Single(Load().AllowedExecutables));
        }
        else
        {
            Assert.Throws<InvalidDataException>(Load);
        }
    }
}
