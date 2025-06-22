using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using AutoFixture;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Messaging;
using Percolator.Messaging;

namespace Percolator.ApplicationTests
{
    [TestFixture]
    public class GroupServiceTests
    {
        private Fixture _fixture;
        private Mock<IMessageStore> _mockMessageStore;
        private GroupService _sut;

        [SetUp]
        public void Setup()
        {
            _fixture = new Fixture();
            _mockMessageStore = new Mock<IMessageStore>();
            _sut = new GroupService(_mockMessageStore.Object);
        }

        [Test]
        public async Task CreateGroupAsync_WithValidData_Succeeds()
        {
            // Arrange
            var groupName = _fixture.Create<string>();
            var memberIds = _fixture.CreateMany<string>(3).ToList();

            // Act
            var group = await _sut.CreateGroupAsync(groupName, memberIds);

            // Assert
            group.Name.Should().Be(groupName);
            group.MemberIds.Should().BeEquivalentTo(memberIds);
            _mockMessageStore.Verify(s => s.StoreGroupAsync(It.Is<Group>(g => g.Name == groupName)), Times.Once);
        }

        [Test]
        public void AddMemberToGroupAsync_WhenRequesterIsMember_Succeeds()
        {
            // Arrange
            var group = _fixture.Create<Group>();
            var requesterId = group.MemberIds.First();
            var newMemberId = _fixture.Create<string>();
            _mockMessageStore.Setup(s => s.GetGroupAsync(group.Id)).ReturnsAsync(group);

            // Act
            Func<Task> act = () => _sut.AddMemberToGroupAsync(group.Id, requesterId, newMemberId);

            // Assert
            act.Should().NotThrowAsync();
            _mockMessageStore.Verify(s => s.UpdateGroupAsync(It.Is<Group>(g => g.MemberIds.Contains(newMemberId))), Times.Once);
        }

        [Test]
        public void AddMemberToGroupAsync_WhenRequesterIsNotMember_ThrowsSecurityException()
        {
            // Arrange
            var group = _fixture.Create<Group>();
            var requesterId = _fixture.Create<string>(); // Not a member
            var newMemberId = _fixture.Create<string>();
            _mockMessageStore.Setup(s => s.GetGroupAsync(group.Id)).ReturnsAsync(group);

            // Act
            Func<Task> act = () => _sut.AddMemberToGroupAsync(group.Id, requesterId, newMemberId);

            // Assert
            act.Should().ThrowAsync<SecurityException>().WithMessage("Only a current member can add a new member to the group.");
        }

        [Test]
        public void RemoveMemberFromGroupAsync_WhenRequesterIsNotMember_ThrowsSecurityException()
        {
            // Arrange
            var group = _fixture.Create<Group>();
            var requesterId = _fixture.Create<string>(); // Not a member
            var memberToRemoveId = group.MemberIds.First();
            _mockMessageStore.Setup(s => s.GetGroupAsync(group.Id)).ReturnsAsync(group);

            // Act & Assert
            Assert.ThrowsAsync<SecurityException>(() => _sut.RemoveMemberFromGroupAsync(group.Id, requesterId, memberToRemoveId));
        }

        [Test]
        public async Task RenameGroupAsync_WhenRequesterIsMember_RenamesGroup()
        {
            // Arrange
            var group = _fixture.Create<Group>();
            var requesterId = group.MemberIds.First();
            var newName = _fixture.Create<string>();

            _mockMessageStore.Setup(s => s.GetGroupAsync(group.Id)).ReturnsAsync(group);

            // Act
            await _sut.RenameGroupAsync(group.Id, requesterId, newName);

            // Assert
            group.Name.Should().Be(newName);
            _mockMessageStore.Verify(s => s.UpdateGroupAsync(group), Times.Once);
        }

        [Test]
        public void RenameGroupAsync_WhenRequesterIsNotMember_ThrowsSecurityException()
        {
            // Arrange
            var group = _fixture.Create<Group>();
            var requesterId = _fixture.Create<string>(); // Not a member
            var newName = _fixture.Create<string>();

            _mockMessageStore.Setup(s => s.GetGroupAsync(group.Id)).ReturnsAsync(group);

            // Act & Assert
            Assert.ThrowsAsync<SecurityException>(() => _sut.RenameGroupAsync(group.Id, requesterId, newName));
        }

        [Test]
        public void RenameGroupAsync_WithInvalidName_ThrowsArgumentException()
        {
            // Arrange
            var group = _fixture.Create<Group>();
            var requesterId = group.MemberIds.First();
            var newName = " "; // Invalid name

            _mockMessageStore.Setup(s => s.GetGroupAsync(group.Id)).ReturnsAsync(group);

            // Act & Assert
            Assert.ThrowsAsync<ArgumentException>(() => _sut.RenameGroupAsync(group.Id, requesterId, newName));
        }
    }
}
