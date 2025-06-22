using System;
using System.Collections.Generic;
using System.Linq;

namespace Percolator.Messaging
{
    public class Group
    {
        public Guid Id { get; }
        public string Name { get; private set; }
        private HashSet<string> _memberIds;
        public IReadOnlyCollection<string> MemberIds => _memberIds;

        public Group(Guid id, string name, IEnumerable<string>? memberIds)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Group name cannot be empty.", nameof(name));
            }

            if (memberIds is null || !memberIds.Any())
            {
                throw new ArgumentException("A group must be created with at least one member.", nameof(memberIds));
            }

            Id = id;
            Name = name;
            _memberIds = new HashSet<string>(memberIds); // Ensure uniqueness
        }

        public void Rename(string newName)
        {
            if (string.IsNullOrWhiteSpace(newName))
            {
                throw new ArgumentException("Group name cannot be empty.", nameof(newName));
            }
            Name = newName;
        }

        public void AddMember(string memberId)
        {
            if (!_memberIds.Add(memberId))
            {
                throw new InvalidOperationException("Member is already part of the group.");
            }
        }

        public void RemoveMember(string memberId)
        {
            if (!_memberIds.Remove(memberId))
            {
                throw new InvalidOperationException("Member is not part of the group.");
            }
        }
    }
}
