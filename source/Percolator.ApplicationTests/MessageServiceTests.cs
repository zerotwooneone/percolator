using System;
using System.Collections.Generic;
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
    public class MessageServiceTests
    {
        private Fixture _fixture;
        private Mock<IMessageStore> _mockMessageStore;
        private Mock<IGroupService> _mockGroupService;
        private MessageService _sut;

        [SetUp]
        public void Setup()
        {
            _fixture = new Fixture();
            _mockMessageStore = new Mock<IMessageStore>();
            _mockGroupService = new Mock<IGroupService>();
            _sut = new MessageService(_mockMessageStore.Object, _mockGroupService.Object);
        }

        [Test]
        public async Task EditDirectMessageAsync_WhenEditorIsSender_Succeeds()
        {
            // Arrange
            var message = _fixture.Create<DirectMessage>();
            var newContent = _fixture.Create<string>();
            _mockMessageStore.Setup(s => s.GetDirectMessageAsync(message.Id)).ReturnsAsync(message);

            // Act
            await _sut.EditDirectMessageAsync(message.Id, message.SenderId, newContent);

            // Assert
            _mockMessageStore.Verify(s => s.UpdateDirectMessageAsync(It.Is<DirectMessage>(m => 
                m.Id == message.Id &&
                m.Content == newContent &&
                m.IsEdited == true
            )), Times.Once);
        }

        [Test]
        public void EditDirectMessageAsync_WhenEditorIsNotSender_ThrowsSecurityException()
        {
            // Arrange
            var message = _fixture.Create<DirectMessage>();
            var maliciousEditorId = _fixture.Create<string>();
            _mockMessageStore.Setup(s => s.GetDirectMessageAsync(message.Id)).ReturnsAsync(message);

            // Act
            Func<Task> act = () => _sut.EditDirectMessageAsync(message.Id, maliciousEditorId, "new content");

            // Assert
            act.Should().ThrowAsync<SecurityException>().WithMessage("Only the sender can edit the message.");
        }

        [Test]
        public void EditGroupMessageAsync_WhenEditorIsNotSender_ThrowsSecurityException()
        {
            // Arrange
            var message = _fixture.Create<GroupMessage>();
            var maliciousEditorId = _fixture.Create<string>();
            _mockMessageStore.Setup(s => s.GetGroupMessageAsync(message.Id)).ReturnsAsync(message);

            // Act
            Func<Task> act = () => _sut.EditGroupMessageAsync(message.Id, maliciousEditorId, "new content");

            // Assert
            act.Should().ThrowAsync<SecurityException>().WithMessage("Only the sender can edit the message.");
        }

        [Test]
        public void SendGroupMessageAsync_WhenSenderIsNotInGroup_ThrowsSecurityException()
        {
            // Arrange
            var group = _fixture.Create<Group>();
            var nonMemberSenderId = _fixture.Create<string>();
            var content = _fixture.Create<string>();
            _mockMessageStore.Setup(s => s.GetGroupAsync(group.Id)).ReturnsAsync(group);

            // Act
            Func<Task> act = () => _sut.SendGroupMessageAsync(group.Id, nonMemberSenderId, content);

            // Assert
            act.Should().ThrowAsync<SecurityException>().WithMessage("Sender is not a member of the group.");
        }

        [Test]
        public async Task AnnotateGroupMessageAsync_WhenPeerIsMember_Succeeds()
        {
            // Arrange
            var group = _fixture.Create<Group>();
            var message = new GroupMessage(
                _fixture.Create<Guid>(),
                group.Id,
                _fixture.Create<string>(),
                _fixture.Create<string>(),
                _fixture.Create<DateTime>());
            var peerId = group.MemberIds.First();
            var emoji = AllowedAnnotations.Heart;

            _mockMessageStore.Setup(s => s.GetGroupMessageAsync(message.Id)).ReturnsAsync(message);
            _mockGroupService.Setup(s => s.GetGroupDetailsAsync(message.GroupId)).ReturnsAsync(group);

            // Act
            await _sut.AnnotateGroupMessageAsync(message.Id, peerId, emoji);

            // Assert
            _mockMessageStore.Verify(s => s.UpdateGroupMessageAsync(It.Is<GroupMessage>(m => m.Annotations.Any(a => a.Emoji == emoji))), Times.Once);
        }

        [Test]
        public void AnnotateGroupMessageAsync_WhenPeerIsNotMember_ThrowsSecurityException()
        {
            // Arrange
            var group = _fixture.Create<Group>();
            var message = new GroupMessage(
                _fixture.Create<Guid>(),
                group.Id,
                _fixture.Create<string>(),
                _fixture.Create<string>(),
                _fixture.Create<DateTime>());
            var nonMemberPeerId = _fixture.Create<string>();
            var emoji = AllowedAnnotations.Heart;

            _mockMessageStore.Setup(s => s.GetGroupMessageAsync(message.Id)).ReturnsAsync(message);
            _mockGroupService.Setup(s => s.GetGroupDetailsAsync(message.GroupId)).ReturnsAsync(group);

            // Act & Assert
            Assert.ThrowsAsync<SecurityException>(() =>
                _sut.AnnotateGroupMessageAsync(message.Id, nonMemberPeerId, emoji));
        }
    }
}
