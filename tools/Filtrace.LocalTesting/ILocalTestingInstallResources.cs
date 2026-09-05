// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Defines the resource-scoped operations ordered by the installation coordinator.
/// </summary>
internal interface ILocalTestingInstallResources
{
    /// <summary>
    ///  Captures the immutable target baseline before a fresh installation.
    /// </summary>
    /// <param name="plan">The target's fixed resource plan.</param>
    /// <returns>The captured MCP, skill, and created-directory baseline.</returns>
    LocalTestingBaseline CaptureBaseline(ResourcePlan plan);

    /// <summary>
    ///  Verifies that persisted baseline artifacts still match their recorded identity.
    /// </summary>
    /// <param name="plan">The target's fixed resource plan.</param>
    /// <param name="baseline">The immutable baseline to validate.</param>
    void ValidateBaseline(ResourcePlan plan, LocalTestingBaseline baseline);

    /// <summary>
    ///  Installs the prepared CLI, optionally replacing a CLI owned by existing local-testing state.
    /// </summary>
    /// <param name="plan">The target's fixed resource plan.</param>
    /// <param name="inputs">The validated source-built inputs.</param>
    /// <param name="replaceExisting">Whether an existing private CLI may be replaced.</param>
    /// <returns>The installed package identity.</returns>
    CliInstallation InstallCli(
        ResourcePlan plan,
        LocalTestingInstallInputs inputs,
        bool replaceExisting);

    /// <summary>
    ///  Publishes the prepared MCP server into the target configuration.
    /// </summary>
    /// <param name="plan">The target's fixed resource plan.</param>
    /// <param name="inputs">The validated source-built inputs.</param>
    void PublishMcp(ResourcePlan plan, LocalTestingInstallInputs inputs);

    /// <summary>
    ///  Publishes the prepared skill while preserving the consumer overlay.
    /// </summary>
    /// <param name="plan">The target's fixed resource plan.</param>
    /// <param name="inputs">The validated source-built inputs.</param>
    void PublishSkill(ResourcePlan plan, LocalTestingInstallInputs inputs);
}
