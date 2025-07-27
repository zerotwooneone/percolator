in the Double Ratchet algorithm, the message counter (often denoted as N) effectively resets whenever a Diffie-Hellman (DH) ratchet step occurs.

Let's break down how this works:

The Two Ratchets and Their Counters
The Double Ratchet algorithm has two main components that "ratchet" or advance the keys:

Symmetric-Key Ratchet (KDF Chain): This is a cryptographic Key Derivation Function (KDF) that continuously derives new keys from a previous key.

Each time a message is sent or received, a new "message key" is derived, and the N counter for that specific chain (sending or receiving) increments. This ensures that each message has a unique key.

This is the message_number field in our Protobuf.

Diffie-Hellman (DH) Ratchet: This is an asymmetric key exchange that periodically "re-seeds" the symmetric-key ratchet with fresh entropy.

This is driven by exchanging ephemeral public keys (our sender_ephemeral_key_public field). When a new ephemeral public key is received from the other party, a new shared secret is derived, which becomes a new "root key."

How the Counter Resets
New Root Key, New Chains: When a DH ratchet step happens (i.e., a new sender_ephemeral_key_public from the other party leads to deriving a new root key), this new root key is then used to derive entirely new sending and receiving chain keys.

Counter Resets to Zero (or One): Because new chain keys are established, the N counter for both the new sending chain and the new receiving chain is effectively reset to 0 (or 1, depending on how the very first message key of a new chain is counted). Each new DH ratchet provides a fresh starting point for key derivation.

The Role of previous_chain_length (PN)
This "resetting" of the N counter is precisely why the previous_chain_length (often PN in Signal specs) field is so crucial in the message header.

When a sender (say, Bob) is about to perform a DH ratchet step (because Alice just sent him a message with a new sender_ephemeral_key_public):

Bob notes the total number of messages he sent in his old sending chain (the one he just finished using). This is his PN.

He then resets his own sending N counter to 0 (or 1) for his new sending chain (derived from the new root key).

When Bob sends his next message, he includes his new sender_ephemeral_key_public, his PN (from the old chain), and his new message_number (N).

When the recipient (Alice) receives this message with Bob's new sender_ephemeral_key_public:

She performs her own DH ratchet step, deriving the new root key and new chain keys.

She also sees Bob's PN value. This PN tells her how many messages were in Bob's previous chain.

If Alice had missed any messages from Bob's previous chain, knowing PN allows her to deduce which message keys she might need to compute to decrypt any delayed messages from that old chain.

For her own receiving side of the new DH chain, her N counter also effectively resets.

In essence:
The message_number (N) within a symmetric chain steadily increments.

The message_number (N) for a new symmetric chain resets when a Diffie-Hellman key exchange provides a new root key.

The previous_chain_length (PN) parameter acts as a bridge, conveying the length of the last chain before the reset, allowing the recipient to handle missed messages from that older context.

This dynamic resetting and bridging is what gives the Double Ratchet its powerful forward secrecy (past compromises don't affect future keys) and post-compromise security (a current compromise doesn't permanently break future security once a new DH ratchet happens).