// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using FluentAssertions;
using Xunit;

namespace KamsoraAPM.Agent.Tests;

public class AgentBuildSmokeTests
{
    [Fact]
    public void Agent_version_is_populated()
    {
        KamsoraApmAgent.Version.Should().NotBeNullOrWhiteSpace();
        // Semver-ish: "major.minor..." - stays valid across version bumps.
        KamsoraApmAgent.Version.Should().MatchRegex(@"^\d+\.\d+");
    }
}
