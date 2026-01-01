# Simulator overhaul
Let's overhaul the simulator window. The goal is to wire up the accept/reject handshake and request handshake functionality now, but build the UI to support future features

## Simulator Window
Has a list of conversations. 
Each conversation represents either a simulated peer (direct conversation), or a group conversation of at least 3 peers where one of the peers is the main window. 
Each peer represents a potential peer that could create a x3dh session with the main window. When a session is established the peer will be able to send messages to the main window.
The list has buttons for both add a new peer and remove the selected peer.
Conversations can be multi-selected in the list. 
If the selection in the list contains at least two simulated peers which have successfully handshaked with the main window, a group conversation can be created from these peers (which includes the main window).
Adding a new peer requires either providing a DnsEndpoint or specifying another already existing simulated peer from the list to act as the relay for this peer.

Changes to the list of peers or to any peer themselves cause the changes to be saved to disk (the save is debounced to allow rapid changes).

## Remote Peer States
Each simulated peer represent a state machine. The state machine includes states 
- Ready
  - means that this peer can submit a handshake request to the main window
  - this is the default state 
  - we return to this state if the remote peer rejects our handshake request
- PendingMainAccept
  - this state is entered when this simulated peer has sent a handshake request
  - during this state there is nothing the remote peer can do other than send further handshake requests
- PendingSimAccept
  - this state is entered when this simulated peer has received a handshake request from the main window
  - during this state there will be an accept and a reject handshake buttons which will respond to the main window
- SessionEstablished
  - this state is entered when either this peer or the main window has accepted the handshake request and a session has been established
  - during this state there will be a send button and a text box which will send a message to the main window
- Offline
  - during this state the peer is offline it will not be able to send or receive messages

### Peer UI
- each simulated peer has a toggle to switch between online and offline
- each peer has an optional name that defaults to a random string
- each peer has a button to show a dialog of other known peers
  - on the dialog, other simulated peers can be added and removed from a list
  - the main-window peer cannot be added to this list without first requesting a handshake from the main window
  - these added peers represent those that will be returned if the main window requests DHT nearest peers from this simulated peer
  - this dialog will also have a button for creating a new group conversation - at least one other simulated peer (which has handshaked with the main window and this peer) must be selected and the created group will always include the main window
- each peer has a button to request a handshake from the main window
- each peer has a button to add a new peer that will relay its messages through this peer
- each peer has a button that when clicked will return "delivered" messages back to the main window for each message the main window has sent to this peer, but which has not yet been received by this peer
- - each peer has a button that when clicked will return "read receipt" messages back to the main window for each message the main window has sent to this peer, but which has not yet been received by this peer

## Architecture
- Idiomatic MVVM WPF
- ViewModels use BindableReactiveProperty and Bind to <PropertyName>.Value for properties that are expected to change over time
- Global Styles are defined in styles.xaml
  - a base style must be defined once for each control type in use
  - a default (unnamed) style for each control type must inherit from the base style for that type
- custom styles can be defined for specific appearances relative to where the style is needed, but must be based on a base style