using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Profiles.Releases;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Profiles.Releases
{
    [TestFixture]
    public class ReleaseProfileServiceFixture : CoreTest<ReleaseProfileService>
    {
        private List<ReleaseProfile> _stored;

        [SetUp]
        public void Setup()
        {
            _stored = new List<ReleaseProfile>
            {
                new ReleaseProfile { Id = 1, Enabled = true, Required = new List<string> { "x264" }, Tags = new HashSet<int>() }
            };

            Mocker.GetMock<IReleaseProfileRepository>()
                  .Setup(s => s.All())
                  .Returns(() => new List<ReleaseProfile>(_stored));
        }

        [Test]
        public void should_load_profiles_once()
        {
            Subject.EnabledForTags(new HashSet<int>(), 0).Should().HaveCount(1);
            Subject.AllForTags(new HashSet<int> { 1 }).Should().HaveCount(1);
            Subject.All().Should().HaveCount(1);

            Mocker.GetMock<IReleaseProfileRepository>().Verify(v => v.All(), Times.Once());
        }

        [Test]
        public void should_return_changed_profiles_after_add_update_and_delete()
        {
            Subject.EnabledForTags(new HashSet<int>(), 0).Should().HaveCount(1);

            var added = new ReleaseProfile { Id = 2, Enabled = true, Tags = new HashSet<int>() };
            _stored.Add(added);
            Subject.Add(added);

            Subject.EnabledForTags(new HashSet<int>(), 0).Should().HaveCount(2);

            var disabled = new ReleaseProfile { Id = 1, Enabled = false, Tags = new HashSet<int>() };
            _stored[0] = disabled;
            Subject.Update(disabled);

            Subject.EnabledForTags(new HashSet<int>(), 0).Should().ContainSingle(p => p.Id == 2);

            _stored.Remove(added);
            Subject.Delete(2);

            Subject.EnabledForTags(new HashSet<int>(), 0).Should().BeEmpty();
        }
    }
}
