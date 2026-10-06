using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.FileList;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.ThingiProvider.Events;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.IndexerTests
{
    public class IndexerServiceFixture : DbTest<IndexerFactory, IndexerDefinition>
    {
        private List<IIndexer> _indexers;

        [SetUp]
        public void Setup()
        {
            _indexers = new List<IIndexer>();

            _indexers.Add(Mocker.Resolve<Newznab>());
            _indexers.Add(Mocker.Resolve<FileList>());

            Mocker.SetConstant<IEnumerable<IIndexer>>(_indexers);
        }

        [Test]
        public void should_remove_missing_indexers_on_startup()
        {
            var repo = Mocker.Resolve<IndexerRepository>();

            Mocker.SetConstant<IIndexerRepository>(repo);

            var existingIndexers = Builder<IndexerDefinition>.CreateNew().BuildNew();
            existingIndexers.ConfigContract = nameof(NewznabSettings);

            repo.Insert(existingIndexers);

            Subject.Handle(new ApplicationStartedEvent());

            AllStoredModels.Should().NotContain(c => c.Id == existingIndexers.Id);

            ExceptionVerification.ExpectedWarns(1);
        }

        private void GivenRealRepository()
        {
            Mocker.SetConstant<IIndexerRepository>(Mocker.Resolve<IndexerRepository>());
        }

        private IndexerDefinition GivenStoredIndexer()
        {
            var indexer = Builder<IndexerDefinition>.CreateNew()
                .With(d => d.Id = 0)
                .With(d => d.Implementation = nameof(Newznab))
                .With(d => d.ConfigContract = nameof(NewznabSettings))
                .With(d => d.Settings = new NewznabSettings())
                .With(d => d.Tags = new HashSet<int>())
                .BuildNew();

            return Subject.Create(indexer);
        }

        [Test]
        public void should_return_changed_indexer_after_update()
        {
            GivenRealRepository();
            var indexer = GivenStoredIndexer();

            Subject.Get(indexer.Id).Tags.Should().BeEmpty();

            var updated = Builder<IndexerDefinition>.CreateNew().BuildNew();
            updated.Id = indexer.Id;
            updated.Implementation = indexer.Implementation;
            updated.ConfigContract = indexer.ConfigContract;
            updated.Settings = new NewznabSettings();
            updated.Tags = new HashSet<int> { 5 };

            Subject.Update(updated);
            Subject.Handle(new ProviderUpdatedEvent<IIndexer>(updated));

            Subject.Get(indexer.Id).Tags.Should().BeEquivalentTo(new[] { 5 });
        }

        [Test]
        public void should_find_added_indexer_and_not_deleted_indexer()
        {
            GivenRealRepository();
            Subject.Find(1).Should().BeNull();

            var indexer = GivenStoredIndexer();
            Subject.Handle(new ProviderAddedEvent<IIndexer>(indexer));

            Subject.Find(indexer.Id).Should().NotBeNull();

            Subject.Delete(indexer.Id);
            Subject.Handle(new ProviderDeletedEvent<IIndexer>(indexer.Id));

            Subject.Find(indexer.Id).Should().BeNull();
            Assert.Throws<ModelNotFoundException>(() => Subject.Get(indexer.Id));
        }
    }
}
