// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting.Tests;

[TestClass]
public sealed class LocalTestingOperationClassifierTests
{
    [TestMethod]
    public void Classify_NoStateInstall_ReturnsFreshInstall()
    {
        LocalTestingOperationClassifier.Classify(LocalTestingAction.Install, null)
            .Should().Be(LocalTestingOperation.FreshInstall);
    }

    [TestMethod]
    public void Classify_NoStateRestore_Throws()
    {
        Action action = () =>
            LocalTestingOperationClassifier.Classify(LocalTestingAction.Restore, null);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*requires existing*");
    }

    [TestMethod]
    [DataRow((int)LocalTestingAction.Unknown)]
    [DataRow(99)]
    public void Classify_UnknownAction_Throws(int actionValue)
    {
        LocalTestingAction action = (LocalTestingAction)actionValue;

        Action classify = () => LocalTestingOperationClassifier.Classify(action, null);

        classify.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("action");
    }

    [TestMethod]
    [DataRow((int)LocalTestingStatus.Unknown)]
    [DataRow(99)]
    public void Classify_UnknownStateStatus_Throws(int statusValue)
    {
        LocalTestingStatus status = (LocalTestingStatus)statusValue;

        Action classify = () => LocalTestingOperationClassifier.Classify(
            LocalTestingAction.Restore,
            CreateState(status));

        classify.Should().Throw<InvalidDataException>()
            .WithMessage("*unknown status*");
    }

    [TestMethod]
    [DataRow((int)LocalTestingStatus.Installing, (int)LocalTestingAction.Install, (int)LocalTestingOperation.ResumeInstall)]
    [DataRow((int)LocalTestingStatus.Installing, (int)LocalTestingAction.Restore, (int)LocalTestingOperation.Restore)]
    [DataRow((int)LocalTestingStatus.Active, (int)LocalTestingAction.Install, (int)LocalTestingOperation.Refresh)]
    [DataRow((int)LocalTestingStatus.Active, (int)LocalTestingAction.Restore, (int)LocalTestingOperation.Restore)]
    [DataRow((int)LocalTestingStatus.Restoring, (int)LocalTestingAction.Restore, (int)LocalTestingOperation.Restore)]
    [DataRow((int)LocalTestingStatus.Cleanup, (int)LocalTestingAction.Restore, (int)LocalTestingOperation.CleanupRetry)]
    public void Classify_ValidStateAndAction_ReturnsExpectedOperation(
        int statusValue,
        int actionValue,
        int expectedValue)
    {
        LocalTestingStatus status = (LocalTestingStatus)statusValue;
        LocalTestingAction action = (LocalTestingAction)actionValue;
        LocalTestingOperation expected = (LocalTestingOperation)expectedValue;

        LocalTestingOperationClassifier.Classify(action, CreateState(status))
            .Should().Be(expected);
    }

    [TestMethod]
    [DataRow((int)LocalTestingStatus.Restoring, (int)LocalTestingAction.Install)]
    [DataRow((int)LocalTestingStatus.Cleanup, (int)LocalTestingAction.Install)]
    public void Classify_InvalidStateAndAction_Throws(
        int statusValue,
        int actionValue)
    {
        LocalTestingStatus status = (LocalTestingStatus)statusValue;
        LocalTestingAction action = (LocalTestingAction)actionValue;
        Action classify = () =>
            LocalTestingOperationClassifier.Classify(action, CreateState(status));

        classify.Should().Throw<InvalidOperationException>()
            .WithMessage("*not valid*");
    }

    private static LocalTestingState CreateState(LocalTestingStatus status)
    {
        return TestState.Create(status);
    }
}