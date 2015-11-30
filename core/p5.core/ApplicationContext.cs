/*
 * Phosphorus Five, copyright 2014 - 2015, Thomas Hansen, phosphorusfive@gmail.com
 * Phosphorus Five is licensed under the terms of the MIT license, see the enclosed LICENSE file for details
 */

using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

/// <summary>
///     Main namespace for Phosphorus core functionality.
/// </summary>
namespace p5.core
{
    /// <summary>
    ///     Application context, which your Active Events are raised through.
    /// </summary>
    public class ApplicationContext
    {
        /// <summary>
        ///     Class used as ticket when raising Active Events
        /// </summary>
        [Serializable]
        public class ContextTicket
        {
            /// <summary>
            ///     Initializes a new instance of the <see cref="p5.core.ApplicationContext+ContextTicket"/> class
            /// </summary>
            /// <param name="username">Username</param>
            /// <param name="role">Role</param>
            public ContextTicket(string username, string role, bool isDefault)
            {
                Username = username;
                Role = role;
                IsDefault = isDefault;
            }

            /// <summary>
            ///     Gets the username
            /// </summary>
            /// <value>The username</value>
            public string Username {
                get;
                private set;
            }

            /// <summary>
            ///     Gets the role
            /// </summary>
            /// <value>The password</value>
            public string Role {
                get;
                private set;
            }

            /// <summary>
            ///     Gets whether or not this is the "default Context user"
            /// </summary>
            /// <value>Whethere or not user is "default user"</value>
            public bool IsDefault {
                get;
                private set;
            }
        }

        private readonly ActiveEvents _registeredActiveEvents = new ActiveEvents();
        private readonly Loader.ActiveEventTypes _typesInstanceActiveEvents;
        private ContextTicket _ticket;

        internal ApplicationContext (
            Loader.ActiveEventTypes instanceEvents, 
            Loader.ActiveEventTypes staticEvents, 
            ContextTicket ticket)
        {
            _typesInstanceActiveEvents = instanceEvents;
            InitializeApplicationContext (staticEvents);
            _ticket = ticket;
        }

        /// <summary>
        ///     Gets the Context user ticket
        /// </summary>
        /// <value>The ticket</value>
        public ContextTicket Ticket
        {
            get { return _ticket; }
        }

        /// <summary>
        ///     Returns all Active Events registered within the current ApplicationContext object.
        /// </summary>
        /// <value>The active events.</value>
        public IEnumerable<string> ActiveEvents
        {
            get { return _registeredActiveEvents.GetEvents ().Select (idx => idx.Name); }
        }

        /// <summary>
        ///     Changes the ticket for the context
        /// </summary>
        /// <param name="ticket">New ticket</param>
        public void UpdateTicket (ContextTicket ticket)
        {
            _ticket = ticket;
        }

        /// <summary>
        ///     Determines whether given Active Event is protected or not
        /// </summary>
        /// <returns><c>true</c> if Active Event is protected; otherwise, <c>false</c></returns>
        /// <param name="name">Name.</param>
        public bool IsProtected (string name)
        {
            var evt = _registeredActiveEvents.GetEvents().SingleOrDefault(idx => idx.Name == name);
            if (evt != null)
                return evt.Protected;
            return false;
        }

        /// <summary>
        ///     Registers an instance Active Event listening object.
        /// </summary>
        /// <param name="instance">object to register for Active Event handling</param>
        public void RegisterListeningObject (object instance)
        {
            if (instance == null)
                throw new ArgumentNullException ("instance");

            // Recursively iterating the Type of the object given, until we reach an object where we know there won't exist
            // any Active Events
            var idxType = instance.GetType ();
            while (!idxType.FullName.StartsWith ("System.")) {

                // Checking to see if this type is registered in our list of types that has Active Events
                if (_typesInstanceActiveEvents.Types.ContainsKey (idxType)) {

                    // Retrieving the list of ActiveEvent/MethodInfo  for the currently iterated Type
                    foreach (var idxActiveEvent in _typesInstanceActiveEvents.Types [idxType].Events) {

                        // Adding Active Event
                        _registeredActiveEvents.AddMethod(
                            idxActiveEvent.Attribute.Name,
                            idxActiveEvent.Method,
                            instance,
                            idxActiveEvent.Attribute.Protected);
                    }
                }

                // Continue iteration over Types until we reach a type we know does not have Active Events
                idxType = idxType.BaseType;
            }
        }

        /// <summary>
        ///     Unregisters an instance listening object.
        /// </summary>
        /// <param name="instance">object to unregister</param>
        public void UnregisterListeningObject (object instance)
        {
            if (instance == null)
                throw new ArgumentNullException ("instance");

            // Iterating over the Type until we find a type we know for a fact won't contains Active Events
            var type = instance.GetType ();
            while (!type.FullName.StartsWith ("System.")) {

                // Checking to see if our list of instance Active Events contains the currently iterated Type
                _registeredActiveEvents.RemoveMethod (instance);

                // finding base type, to continue iteration over its Type until we find a Type we're sure of does not 
                // contain Active Event declarations
                type = type.BaseType;
            }
        }

        /// <summary>
        ///     Raises one Active Event.
        /// </summary>
        /// <param name="name">name of Active Event to raise</param>
        /// <param name="pars">arguments to pass into the Active Event</param>
        public Node Raise (string name, Node pars = null)
        {
            return _registeredActiveEvents.Raise (name, pars, this, _ticket);
        }

        /*
         * initializes app context
         */
        private void InitializeApplicationContext (Loader.ActiveEventTypes staticEvents)
        {
            // Looping through each Type in static Active Events given
            foreach (var idxType in staticEvents.Types.Keys) {

                // Looping through each ActiveEvent/MethodInfo tuple in Type
                foreach (var idxAVTypeEvent in staticEvents.Types [idxType].Events) {

                    // Registering Active Event
                    _registeredActiveEvents.AddMethod (
                        idxAVTypeEvent.Attribute.Name, 
                        idxAVTypeEvent.Method, 
                        null, 
                        idxAVTypeEvent.Attribute.Protected);
                }
            }

            // Raising "initialize" Active Event
            Raise ("p5.core.initialize-application-context");
        }
    }
}
