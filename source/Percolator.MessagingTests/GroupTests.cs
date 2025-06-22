using System;
using System.Collections.Generic;
using System.Linq;
using AutoFixture;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Messaging;

namespace Percolator.MessagingTests
{
    [TestFixture]
    public class GroupTests
    {
        private Fixture _fixture;

        [SetUp]
        public void Setup()
        {
            _fixture = new Fixture();
        }

        [Test]
        public void Constructor_WithValidArguments_CreatesGroup()
        {
            // Arrange
            var name = _fixture.Create<string>();
            var memberIds = _fixture.CreateMany<string>(3).ToList();

            // Act
            var group = new Group(Guid.NewGuid(), name, memberIds);

            // Assert
            group.Name.Should().Be(name);
            group.MemberIds.Should().BeEquivalentTo(memberIds);
        }

        [Test]
        public void Constructor_WithDuplicateMemberIds_CreatesGroupWithDistinctMembers()
        {
            // Arrange
            var name = _fixture.Create<string>();
            var memberId = _fixture.Create<string>();
            var duplicateMembers = new List<string> { memberId, memberId };

            // Act
            var group = new Group(Guid.NewGuid(), name, duplicateMembers);

            // Assert
            group.MemberIds.Should().ContainSingle(m => m == memberId);
        }

        [Test]
        public void Constructor_WithNullMemberList_ThrowsArgumentException()
        {
            // Arrange
            var name = _fixture.Create<string>();

            // Act
            Action act = () => new Group(Guid.NewGuid(), name, null);

            // Assert
            act.Should().Throw<ArgumentException>().WithMessage("A group must be created with at least one member. (Parameter 'memberIds')");
        }

        [Test]
        public void Constructor_WithEmptyMemberList_ThrowsArgumentException()
        {
            // Arrange
            var name = _fixture.Create<string>();
            var emptyMembers = new List<string>();

            // Act
            Action act = () => new Group(Guid.NewGuid(), name, emptyMembers);

            // Assert
            act.Should().Throw<ArgumentException>().WithMessage("A group must be created with at least one member. (Parameter 'memberIds')");
        }
    }
}
