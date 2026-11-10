# Percolator.Desktop
This application is strictly controlled by the qms system found in /source/spec

## First Screen

- when the application starts there is a minimal DI container that is used to load the first screen
  - at this point the DBcontext is not available
  - the first screen allows the user to either close the application (standard OS close button), enter a password
  - the password may be blank if the user has chosen to skip password protection (in which case the db is encrypted using a value obscured only by an empty string as the password)
  - the application also has a "duress" password feature that, when the duress password is entered, the db encryption key is securely erased (and replaced with a new key encrypted with the duress password) and a new database is spun up (encrypted with the duress password) and the application runs as usual with the empty database

## Second Screen

- the second screen appears when the database (or the duress database) is loaded and a smoke test passes
- after the database smoke test has passed, a second (inner) DI container is created and the second screen is loaded from that
- the application does not expose anything to the network before the user has selected one or more identities from the second screen
- the second screen shows the user's identities and allows the user to select one or more identities
  - each identity will display the profile picture and display name for the identity 
  - each identity will display the last known port of the identity and an icon will appear next to the port if that port is not available
- the second screen also allows the user to add a new identity
- the second screen also allows the user to remove an identity
  - clicking the remove button will prompt the user to confirm the action by typing the identity name (the identity name will be displayed in the prompt in a form that can be copied and pasted)
  - removing an identity will remove all channels and chats associated with that identity
- the second screen also allows the user to edit an identity
- each identity must be bound to a unique (on the machine) network port before it can be used
- the network port last used for an identity is persisted in the database
- the application will attempt to bind to the network port last used for an identity and if it fails, it will attempt to bind to the next available port from a range of port for the percolator application, the user can override to use a port outside of this range
- the application will not start until all selected identities have been successfully bound to a network port
- if there are no available identities, the application will not start and the user will be guided to add an identity
- the add/edit identity form will be based on the encrypted profile feature found in Percolator.Domain
- 

## Third Screen

- the Third screen is a shell that contains a menu bar and a content area
- the Third screen is inspired by Slack and Discord in that there is a left sidebar that contains a list of channels (in percolator a direct connection to a peer or a relay establishes a channel) and a right/secondary (adjacent to the left sidebar) sidebar that contains a list of chats
- left sidebar
  - one list of direct connections to peers (if any) combines with a list of relays (if any)
  - the user can drag to move items up and down the list and their position is persisted in the database
  - the user can right click on an item to open a context menu that allows them to take actions related to the type of item (direct vs relay)
  - the user can click/select on an item to change the content of the right sidebar
- right sidebar
  - the right sidebar contains a list of chats
    - for a direct connection to a peer, there is only one chat possible - if no chat exists, a new chat is created only when the user sends or receives a message
    - for a relay, there is a chat for each peer or group that is known via the relay
  - the user can drag to move items up and down the list and their position is persisted in the database
  - the user can right click on an item to open a context menu that allows them to take actions related to the type of item (individual chat vs group chat)
  - the user can click/select on an item to change the content of the right sidebar
- Identity Area
  - at the bottom of the left sidebar, and spanning the right sidebar, there is an identity area
  - the identity area contains a profile picture and display name for the currently selected identity
  - the profile picture is displayed within a control that makes it clear that it is clickable
    - when clicked, the user is taken to the edit identity form
    - this version of the edit identity form has more technical features related to key management
  - also contains a control that indicates if there are notifications from other active identities

## Style

- the application is an Avalonia application
- the application has a modern, minimalistic, and professional look and feel
- the application has a consistent color scheme and typography
- the application has a consistent layout and spacing
- the application has a consistent button and control style
