﻿using ApprovalTests;
using ApprovalTests.Namers;
using ApprovalTests.Reporters;
using AspNetUpgrade.Actions.ProjectJson;
using NUnit.Framework;

namespace AspNetUpgrade.Tests.ProjectJson
{

    [UseReporter(typeof(DiffReporter))]
    [TestFixture]
    public class MigrateDnxFrameworksJsonTests
    {

        public MigrateDnxFrameworksJsonTests()
        {

        }



        [TestCase("LibraryProject", TestProjectJsonContents.LibraryProjectRc1)]
        [TestCase("WebApplicationProject", TestProjectJsonContents.WebApplicationProject)]
        [Test]
        public void Can_Apply(string scenario, string json)
        {

            using (ApprovalResults.ForScenario(scenario))
            {
                // arrange
                var testFileUpgradeContext = new TestJsonBaseProjectUpgradeContext(json, null, null);
                var sut = new MigrateDnxFrameworksToNetFramework452Json();

                // act
                sut.Apply(testFileUpgradeContext);
                testFileUpgradeContext.SaveChanges();

                // assert.
                var modifiedContents = testFileUpgradeContext.ModifiedProjectJsonContents;
                Approvals.VerifyJson(modifiedContents);

            }

           
        }

     



    }
}