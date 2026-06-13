// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using FluentAssertions;
using Xunit;

namespace KamsoraAPM.Storage.Tests;

public class StorageBuildSmokeTests
{
    [Fact]
    public void Schema_version_is_set()
    {
        KamsoraApmStorage.SchemaVersion.Should().Be(1);
    }
}
