/*
 * Phosphorus Five, copyright 2014 - 2015, Thomas Hansen, phosphorusfive@gmail.com
 * Phosphorus Five is licensed under the terms of the MIT license, see the enclosed LICENSE file for details.
 */

using System;
using System.IO;
using System.Web;
using System.Linq;
using System.Web.UI;
using System.Collections;
using System.Configuration;
using System.Collections.Generic;
using p5.exp;
using p5Web = p5.web.ui.common;
using p5.core;
using p5.ajax.core;
using p5.webapp.code;
using p5.ajax.widgets;

/// <summary>
///     Main namespace for your Application Pool.
/// </summary>
namespace p5.webapp
{
    using pf = p5.ajax.widgets;

    /// <summary>
    ///     Main .aspx web page for your Application Pool.
    /// </summary>
    public partial class Default : AjaxPage
    {
        // Main container for all widgets
        protected pf.Container cnt;

        // Application Context for page life cycle
        private ApplicationContext _context;

        // Used for locking access to password file
        private object _passwordFileLocker = new object();

        /*
         * Used as storage for Widget Ajax Events
         */
        private WidgetEventStorage WidgetAjaxEventStorage
        {
            get {
                if (ViewState["_WidgetAjaxEventStorage"] == null)
                    ViewState["_WidgetAjaxEventStorage"] = new WidgetEventStorage ();
                return ViewState["_WidgetAjaxEventStorage"] as WidgetEventStorage;
            }
        }

        /*
         * Used as storage for Widget Lambda Events
         */
        private WidgetEventStorage WidgetLambdaEventStorage
        {
            get {
                if (ViewState["_WidgetLambdaEventStorage"] == null)
                    ViewState["_WidgetLambdaEventStorage"] = new WidgetEventStorage ();
                return ViewState["_WidgetLambdaEventStorage"] as WidgetEventStorage;
            }
        }

        /*
         * Used for user Context Ticket (currently logged in Context user)
         */
        private ApplicationContext.ContextTicket Ticket
        {
            get {
                if (Session["_ApplicationContext.ContextTicket"] == null) {

                    // No user is logged in, using default impersonated user
                    var configuration = ConfigurationManager.GetSection("activeEventAssemblies") as ActiveEventAssemblies;
                    Session["_ApplicationContext.ContextTicket"] = 
                        new ApplicationContext.ContextTicket (
                            configuration.DefaultContextUsername, 
                            configuration.DefaultContextRole, 
                            true);
                }
                return Session["_ApplicationContext.ContextTicket"] as ApplicationContext.ContextTicket;
            }
            set { 
                Session["_ApplicationContext.ContextTicket"] = value;
            }
        }

        #region [ -- Page overrides and initializers, plus login of user -- ]

        /*
         * Overridden to create context, and do other types of initialization, such as mapping up our Page_Load event,
         * URL-rewriting, and so on.
         */
        protected override void OnInit (EventArgs e)
        {
            // Retrieving viewstate entries per session
            ViewStateSessionEntries = int.Parse (ConfigurationManager.AppSettings ["viewstate-per-session-entries"]);

            // Checking to see if we should attempt to login from persistent cookie
            if (Session["_ApplicationContext.ContextTicket"] == null) {

                // Creating our application context for current request first, since we need it when trying to login from cookie
                _context = Loader.Instance.CreateApplicationContext (Ticket);

                // Try logging in from cookie, notice if this is successful, we will have ANOTHER ApplicationContext replace our previous one
                TryLoginFromPersistentCookie();
            } else {

                // Creating our application context for current request
                _context = Loader.Instance.CreateApplicationContext (Ticket);
            }

            // Registering "this" web page as listener object, since page contains many Active Event handlers itself
            _context.RegisterListeningObject (this);

            // Rewriting path to what was actually requested, such that HTML form element doesn't become garbage ...
            // this ensures that our HTML form element stays correct. basically "undoing" what was done in Global.asax.cs
            // in addition, when retrieving request URL later, we get the "correct" request URL, and not the URL to "Default.aspx"
            HttpContext.Current.RewritePath ((string) HttpContext.Current.Items ["__p5_original_url"]);

            // mapping up our Page_Load event for initial loading of web page
            if (!IsPostBack)
                Load += Page_LoadInitialLoading;

            // call base
            base.OnInit (e);
        }

        /*
         * Invoked only for the initial request of our web page, to make sure we load up our UI according to which URL is requested.
         * Not invoked during any consecutive POST requests
         */
        private void Page_LoadInitialLoading (object sender, EventArgs e)
        {
            // raising our [p5.web.load-ui] Active Event, creating the node to pass in first
            // where the [_form] node becomes the name of the form requested
            var args = new Node ("p5.web.load-ui");
            args.Add (new Node ("_form", (string) HttpContext.Current.Items ["__p5_original_url"]));

            // invoking the Active Event that actually loads our UI, now with a [_file] node, and possibly an [_args] node
            _context.Raise ("p5.web.load-ui", args);
        }

        #endregion

        #region [ -- Creating and deleting Widgets -- ]

        /// <summary>
        ///     Creates a web widget.
        /// </summary>
        /// <param name="context">Context for current request</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "create-widget")]
        [ActiveEvent (Name = "create-void-widget")]
        [ActiveEvent (Name = "create-literal-widget")]
        private void create_widget (ApplicationContext context, ActiveEventArgs e)
        {
            var splits = e.Name.Split(new char[] {'-'}, StringSplitOptions.RemoveEmptyEntries);
            string type = splits.Length == 2 ? "container" : splits[1];
            CreateWidget (context, e.Args, type);
        }

        /// <summary>
        ///     Checks if the given widget(s) exist
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "widget-exist")]
        private void widget_exist (ApplicationContext context, ActiveEventArgs e)
        {
            // making sure we clean up and remove all arguments passed in after execution
            using (new p5.core.Utilities.ArgsRemover(e.Args)) {

                // loping through all control ID's given
                foreach (var widgetId in XUtil.Iterate<string> (e.Args, context)) {

                    e.Args.Add(widgetId, FindControl<Control>(widgetId, Page) != null);
                }
                e.Args.Value = !e.Args.Children.Where(ix => !ix.Get<bool> (context)).GetEnumerator().MoveNext();
            }
        }

        /// <summary>
        ///     Deletes the given widget(s) entirely.
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "delete-widget")]
        private void delete_widget (ApplicationContext context, ActiveEventArgs e)
        {
            // loping through all control ID's given
            foreach (var widget in FindWidgets<Control> (e.Args)) {

                // actually removing widget from Page control collection, and persisting our change
                var parent = widget.Parent as pf.Container;
                if (parent == null)
                    throw new ArgumentException("You cannot delete a widget who's parent is not a P5.Ajax Container widget. Tried to delete; " + widget.ID + " which is of type " + widget.GetType().FullName);

                // Removing all events, both "lambda" and "ajax"
                RemoveAllEventsRecursive(widget);

                // Removing widget itself
                parent.RemoveControlPersistent (widget);
            }
        }

        /// <summary>
        ///     Clears the given widget, removing all its children widgets.
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "clear-widget")]
        private void clear_widget (ApplicationContext context, ActiveEventArgs e)
        {
            // Looping through all control ID's given
            foreach (var widget in FindWidgetsThrow<pf.Container> (e.Args, "clear-widget")) {

                // Then looping through all of its children controls
                foreach (Control innerCtrl in new ArrayList(widget.Controls)) {

                    // Removing all events, both "lambda" and "ajax"
                    RemoveAllEventsRecursive(innerCtrl);

                    // Actually removing widget from Page control collection, and persisting our change
                    widget.RemoveControlPersistent (innerCtrl);
                }
            }
        }

        #endregion

        #region [ -- Retrieving Widgets -- ]

        /// <summary>
        ///     Returns the ID and type of the given widget's parent.
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "get-parent-widget")]
        private void get_parent_widget (ApplicationContext context, ActiveEventArgs e)
        {
            // Making sure we clean up and remove all arguments passed in after execution
            using (new p5.core.Utilities.ArgsRemover (e.Args, true)) {

                // loping through all control ID's given
                foreach (var widget in FindWidgets <Control> (e.Args)) {

                    var parent = widget.Parent;
                    string type = parent.GetType().FullName;
                    if (parent is Widget)
                        type = type.Substring(type.LastIndexOf(".") + 1).ToLower();
                    e.Args.Add(type, parent.ID);
                }
            }
        }

        /// <summary>
        ///     Returns the ID and type of the given widget's children.
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "get-children-widgets")]
        private void get_children_widgets (ApplicationContext context, ActiveEventArgs e)
        {
            // Making sure we clean up and remove all arguments passed in after execution
            using (new p5.core.Utilities.ArgsRemover (e.Args, true)) {

                // Looping through all control IDs given
                foreach (var widget in FindWidgets <Control> (e.Args)) {

                    e.Args.Add(widget.ID);
                    foreach (Control idxCtrl in widget.Controls) {

                        // looping through all children of currently iterated widget
                        string type = idxCtrl.GetType().FullName;
                        if (idxCtrl is Widget)
                            type = type.Substring(type.LastIndexOf(".") + 1).ToLower();
                        e.Args.LastChild.Add(type, idxCtrl.ID);
                    }
                }
            }
        }
        
        /// <summary>
        ///     Returns the ID and type of the given widget's children.
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "find-widgets")]
        private void find_widgets (ApplicationContext context, ActiveEventArgs e)
        {
            // Making sure we clean up and remove all arguments passed in after execution
            using (new p5.core.Utilities.ArgsRemover (e.Args, true)) {

                // Looping through all control IDs given
                foreach (var widget in FindWidgetsBy <Widget> (e.Args, FindControl<Widget> (e.Args.GetExValue (context, "cnt"), Page), context)) {

                    e.Args.Add(widget.ID);
                }
            }
        }

        /// <summary>
        ///     Lists all widgets on page.
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "list-widgets")]
        private void list_widgets (ApplicationContext context, ActiveEventArgs e)
        {
            // Making sure we clean up and remove all arguments passed in after execution
            using (new p5.core.Utilities.ArgsRemover (e.Args, true)) {

                // retrieving filter, if any
                var filter = new List<string>(XUtil.Iterate<string>(e.Args, context));
                if (e.Args.Value != null && filter.Count == 0)
                    return; // possibly a filter expression, leading into oblivion

                // recursively retrieving all widgets on page
                ListWidgets(filter, e.Args, cnt);
            }
        }

        #endregion

        #region [ -- Widget properties -- ]

        /// <summary>
        ///     Returns properties and/or attributes requested by caller as children nodes.
        /// </summary>
        /// <param name="context">Application context.</param>
        /// <param name="e">Parameters passed into Active Event.</param>
        [ActiveEvent (Name = "get-widget-property")]
        private void get_widget_property (ApplicationContext context, ActiveEventArgs e)
        {
            // Making sure we clean up and remove all arguments passed in after execution
            using (new p5.core.Utilities.ArgsRemover (e.Args, true)) {

                if (e.Args.Value == null || e.Args.Count == 0)
                    return; // nothing to do here ...

                // looping through all widget IDs given by caller
                foreach (var widget in FindWidgetsThrow<Widget> (e.Args, "get-widget-property")) {

                    // looping through all properties requested by caller
                    foreach (var nameNode in e.Args.Children.ToList ()) {

                        // checking if this is a generic attribute, or a specific property
                        switch (nameNode.Name) {
                            case "":
                                continue; // formatting parameter to expression in main node
                            case "visible":
                                CreatePropertyReturn (e.Args, nameNode, widget, widget.Visible);
                                break;
                            case "invisible-element":
                                CreatePropertyReturn (e.Args, nameNode, widget, widget.InvisibleElement);
                                break;
                            case "element":
                                CreatePropertyReturn (e.Args, nameNode, widget, widget.ElementType);
                                break;
                            case "has-id":
                                CreatePropertyReturn (e.Args, nameNode, widget, !widget.NoIdAttribute);
                                break;
                            case "render-type":
                                CreatePropertyReturn (e.Args, nameNode, widget, widget.RenderType.ToString ());
                                break;
                            default:
                                if (!string.IsNullOrEmpty (nameNode.Name))
                                    CreatePropertyReturn (e.Args, nameNode, widget);
                                break;
                        }
                    }
                }
            }
        }

        /// <summary>
        ///     Sets properties and/or attributes of web widgets.
        /// </summary>
        /// <param name="context">Application context.</param>
        /// <param name="e">Parameters passed into Active Event.</param>
        [ActiveEvent (Name = "set-widget-property")]
        private void set_widget_property (ApplicationContext context, ActiveEventArgs e)
        {
            if (e.Args.Value == null || e.Args.Count == 0)
                return; // nothing to do here ...

            // looping through all widget IDs given by caller
            foreach (var widget in FindWidgetsThrow<Widget> (e.Args, "set-widget-property")) {

                // looping through all properties requested by caller
                foreach (var valueNode in e.Args.Children) {
                    switch (valueNode.Name) {
                        case "":
                            continue; // formatting parameter to expression in main node
                        case "visible":
                            widget.Visible = valueNode.GetExValue<bool> (context);
                            break;
                        case "invisible-element":
                            widget.InvisibleElement = valueNode.GetExValue<string> (context);
                            break;
                        case "element":
                            widget.ElementType = valueNode.GetExValue<string> (context);
                            break;
                        case "has-id":
                            widget.NoIdAttribute = valueNode.GetExValue<bool> (context);
                            break;
                        case "render-type":
                            widget.RenderType = (Widget.RenderingType) Enum.Parse (typeof (Widget.RenderingType), valueNode.GetExValue<string> (context));
                            break;
                        default:
                            widget [valueNode.Name] = valueNode.GetExValue<string> (context);
                            break;
                    }
                }
            }
        }

        /// <summary>
        ///     Removes the properties and/or attributes of web widgets.
        /// </summary>
        /// <param name="context">Application context.</param>
        /// <param name="e">Parameters passed into Active Event.</param>
        [ActiveEvent (Name = "delete-widget-property")]
        private void delete_widget_property (ApplicationContext context, ActiveEventArgs e)
        {
            if (e.Args.Value == null || e.Args.Count == 0)
                return; // nothing to do here ...

            // looping through all widgets
            foreach (var widget in FindWidgetsThrow<Widget> (e.Args, "delete-widget-property")) {

                // looping through each property to remove
                foreach (var nameNode in e.Args.Children) {

                    // verifying property can be removed
                    switch (nameNode.Name) {
                        case "":
                            continue; // formatting parameter to expression in main node
                        case "visible":
                        case "invisible-element":
                        case "element":
                        case "has-id":
                        case "has-name":
                        case "render-type":
                            throw new ArgumentException ("Cannot remove property; '" + nameNode.Name + "' of widget.");
                        default:
                            widget.RemoveAttribute (nameNode.Name);
                            break;
                    }
                }
            }
        }

        /// <summary>
        ///     Lists all existing properties for given web widget.
        /// </summary>
        /// <param name="context">Application context.</param>
        /// <param name="e">Parameters passed into Active Event.</param>
        [ActiveEvent (Name = "list-widget-properties")]
        private void list_widget_properties (ApplicationContext context, ActiveEventArgs e)
        {
            // Making sure we clean up and remove all arguments passed in after execution
            using (new p5.core.Utilities.ArgsRemover (e.Args, true)) {

                if (e.Args.Value == null)
                    return; // Nothing to do here ...

                // Looping through all widgets
                foreach (var widget in FindWidgetsThrow<Widget> (e.Args, "list-widget-properties")) {

                    // Creating our "return node" for currently handled widget
                    Node curNode = e.Args.Add (widget.ID).LastChild;

                    // first listing "static properties"
                    if (!widget.Visible)
                        curNode.Add ("visible", false);
                    if ((widget is Container && widget.ElementType != "div") || (widget is Literal && widget.ElementType != "p"))
                        curNode.Add ("element", widget.ElementType);
                    if (widget.NoIdAttribute)
                        curNode.Add ("has-id", false);

                    // Then the generic attributes
                    foreach (var idxAtr in widget.AttributeKeys) {

                        // We drop the Tag property and all events
                        if (idxAtr == "Tag" || idxAtr.StartsWith ("on"))
                            continue;
                        curNode.Add (idxAtr, widget [idxAtr]);
                    }
                }
            }
        }

        #endregion

        #region [ -- Widget Ajax events -- ]

        /// <summary>
        ///     Returns the given ajax event(s) for the given widget(s).
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "get-widget-ajax-event")]
        private void get_widget_ajax_event (ApplicationContext context, ActiveEventArgs e)
        {
            // Making sure we clean up and remove all arguments passed in after execution
            using (new p5.core.Utilities.ArgsRemover (e.Args, true)) {

                // looping through all widgets
                foreach (var widget in FindWidgetsThrow<Widget> (e.Args, "get-widget-ajax-event")) {

                    // looping through events requested by caller
                    foreach (var idxEventNameNode in new List<Node> (e.Args.Children)) {

                        // Returning lambda object for Widget Ajax event
                        if (WidgetAjaxEventStorage[widget.ID, idxEventNameNode.Name] != null)
                            e.Args.FindOrCreate(widget.ID).Add(WidgetAjaxEventStorage[widget.ID, idxEventNameNode.Name].Clone());
                    }
                }
            }
        }

        /// <summary>
        ///     Changes the given ajax event(s) for the given widget(s).
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "set-widget-ajax-event")]
        private void set_widget_ajax_event (ApplicationContext context, ActiveEventArgs e)
        {
            // looping through all widgets
            foreach (var widget in FindWidgetsThrow<Widget> (e.Args, "set-widget-ajax-event")) {

                // looping through events requested by caller
                foreach (var idxEventNameNode in e.Args.Children) {

                    // Setting Widget's Ajax event to whatever we were given
                    WidgetAjaxEventStorage[widget.ID, idxEventNameNode.Name] = idxEventNameNode.Clone();
                }
            }
        }

        /// <summary>
        ///     Removes the given ajax event(s) for the given widget(s).
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "delete-widget-ajax-event")]
        private void delete_widget_ajax_event (ApplicationContext context, ActiveEventArgs e)
        {
            // looping through all widgets
            foreach (var widget in FindWidgetsThrow<Widget> (e.Args, "delete-widget-ajax-event")) {
                
                // looping through events requested by caller
                foreach (var idxEventNameNode in e.Args.Children) {

                    // Setting Widget's Ajax event to whatever we were given
                    WidgetAjaxEventStorage[widget.ID, idxEventNameNode.Name] = null;

                    // removing widget event attribute
                    widget.RemoveAttribute(idxEventNameNode.Name);
                }
            }
        }
        
        /// <summary>
        ///     Lists all existing ajax events for given widget(s).
        /// </summary>
        /// <param name="context">Application context</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "list-widget-ajax-events")]
        private void list_widget_ajax_events (ApplicationContext context, ActiveEventArgs e)
        {
            // looping through all widgets
            foreach (var widget in FindWidgetsThrow<Widget> (e.Args, "list-widget-ajax-events")) {

                // then looping through all attribute keys, filtering everything out that does not start with "on"
                Node curNode = new Node (widget.ID);
                foreach (var idxAtr in widget.AttributeKeys) {

                    // Checking if this attribute is an event or not
                    if (idxAtr.StartsWith ("on"))
                        curNode.Add (idxAtr);
                }

                // Special handling of [oninit], since it never leaves the server
                if (WidgetAjaxEventStorage[widget.ID, "oninit"] != null)
                    curNode.Add("oninit");

                // Checking if we've got more than zero events for given widget, and if so, adding event node, containing list of events
                if (curNode.Count > 0)
                    e.Args.Add(curNode);
            }
        }
        
        /*
         * common ajax event handler for all widget's events on page
         */
        [WebMethod]
        protected void common_event_handler (pf.Widget sender, pf.Widget.AjaxEventArgs e)
        {
            _context.Raise("eval", WidgetAjaxEventStorage[sender.ID, e.Name].Clone());
        }

        #endregion

        #region [ -- Widget lambda events -- ]
        
        /// <summary>
        ///     Returns the given lambda event(s) for the given widget(s).
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "get-widget-lambda-event")]
        private void get_widget_lambda_event (ApplicationContext context, ActiveEventArgs e)
        {
            // Making sure we clean up and remove all arguments passed in after execution
            using (new p5.core.Utilities.ArgsRemover (e.Args, true)) {

                // looping through all widgets
                foreach (var widget in FindWidgetsThrow<Widget> (e.Args, "get-widget-lambda-event")) {

                    // looping through events requested by caller
                    foreach (var idxEventNameNode in new List<Node> (e.Args.Children)) {

                        // Returning lambda object for Widget Ajax event
                        if (WidgetLambdaEventStorage[idxEventNameNode.Name, widget.ID] != null) {

                            // We found a Lambda event with that name for that widget
                            var evtNode = WidgetLambdaEventStorage[idxEventNameNode.Name, widget.ID].Clone();
                            evtNode.Name = idxEventNameNode.Name;
                            e.Args.FindOrCreate(widget.ID).Add(evtNode);
                        }
                    }
                }
            }
        }

        /// <summary>
        ///     Changes the given lambda event(s) for the given widget(s).
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "set-widget-lambda-event")]
        private void set_widget_lambda_event (ApplicationContext context, ActiveEventArgs e)
        {
            // looping through all widgets
            foreach (var widget in FindWidgetsThrow<Widget> (e.Args, "set-widget-lambda-event")) {

                // looping through events requested by caller
                foreach (var idxEventNameNode in e.Args.Children) {

                    // Verifying Active Event is not protected
                    if (EventIsProtected (context, idxEventNameNode.Name))
                        throw new ApplicationException(string.Format ("You cannot override Active Event '{0}' since it is protected", e.Args.Name));

                    // Setting Widget's Ajax event to whatever we were given
                    WidgetLambdaEventStorage[idxEventNameNode.Name, widget.ID] = idxEventNameNode.Clone();
                }
            }
        }

        /// <summary>
        ///     Removes the given lambda event(s) for the given widget(s).
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "delete-widget-lambda-event")]
        private void delete_widget_lambda_event (ApplicationContext context, ActiveEventArgs e)
        {
            // looping through all widgets
            foreach (var widget in FindWidgetsThrow<Widget> (e.Args, "delete-widget-lambda-event")) {

                // looping through events requested by caller
                foreach (var idxEventNameNode in e.Args.Children) {

                    // Setting Widget's Ajax event to whatever we were given
                    WidgetLambdaEventStorage[idxEventNameNode.Name, widget.ID] = null;
                }
            }
        }

        /// <summary>
        ///     Lists all existing lambda events for given widget(s).
        /// </summary>
        /// <param name="context">Application context</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "list-widget-lambda-events")]
        private void list_widget_lambda_events (ApplicationContext context, ActiveEventArgs e)
        {
            // looping through all widgets
            foreach (var widget in FindWidgetsThrow<Widget> (e.Args, "list-widget-lambda-events")) {

                // then looping through all attribute keys, filtering everything out that does not start with "on"
                Node curNode = new Node (widget.ID);
                foreach (var idxAtr in WidgetLambdaEventStorage.FindByKey2 (widget.ID)) {

                    // Checking if this attribute is an event or not
                    curNode.Add (idxAtr);
                }

                // Checking if we've got more than zero events for given widget, and if so, adding event node, containing list of events
                if (curNode.Count > 0)
                    e.Args.Add(curNode);
            }
        }
        
        /*
         * Raises Widget specific lambda events
         */
        [ActiveEvent (Name = "")]
        private void null_handler (ApplicationContext context, ActiveEventArgs e)
        {
            // Looping through each lambda event handler for given event
            foreach (var idxLambda in WidgetLambdaEventStorage [e.Name].ToList ()) {

                // Raising Active Event
                XUtil.RaiseEvent (context, e.Args, idxLambda, e.Name);
            }
        }

        #endregion

        #region [ -- ViewState setters and getters -- ]

        /// <summary>
        ///     Sets one or more ViewState object(s).
        /// </summary>
        /// <param name="context">Application context.</param>
        /// <param name="e">Parameters passed into Active Event.</param>
        [ActiveEvent (Name = "set-viewstate")]
        private void set_viewstate (ApplicationContext context, ActiveEventArgs e)
        {
            p5Web.CollectionBase.Set (e.Args, context, delegate (string key, object value) {

                if (key.StartsWith ("_"))
                    throw new ApplicationException (string.Format ("You cannot modify '{0}' viewstate value, since it is protected", key));

                if (value == null) {

                    // removing object, if it exists
                    ViewState.Remove (key);
                } else {

                    // adding object
                    ViewState [key] = value;
                }
            });
        }

        /// <summary>
        ///     Retrieves ViewState object(s).
        /// </summary>
        /// <param name="context">Application context.</param>
        /// <param name="e">Parameters passed into Active Event.</param>
        [ActiveEvent (Name = "get-viewstate")]
        private void get_viewstate (ApplicationContext context, ActiveEventArgs e)
        {
            p5Web.CollectionBase.Get (e.Args, context, key => ViewState [key]);
            e.Args.RemoveAll(
                ix => ix.Name.StartsWith ("_"));
        }

        /// <summary>
        ///     Lists all keys in the Session object.
        /// </summary>
        /// <param name="context">Application context.</param>
        /// <param name="e">Parameters passed into Active Event.</param>
        [ActiveEvent (Name = "list-viewstate")]
        private void list_viewstate (ApplicationContext context, ActiveEventArgs e)
        {
            p5Web.CollectionBase.List (e.Args, context, () => (from object idx in ViewState.Keys select idx.ToString ()).ToList ());
            e.Args.RemoveAll(
                ix => ix.Name.StartsWith ("_"));
        }

        #endregion

        #region [ -- Misc. global helpers -- ]

        /// <summary>
        ///     Logs in a user to be associated with the ApplicationContext
        /// </summary>
        /// <param name="context">Application Context</param>
        /// <param name="e">Active Event arguments</param>
        [ActiveEvent (Name = "login", Protected = true)]
        private void login (ApplicationContext context, ActiveEventArgs e)
        {
            try {
                
                if (Application["_Last-Forms-Login-Attempt-" + Request.UserHostAddress] != null) {

                    // User has previous unsuccessful login attempts to the system, verifying we're not being "hammered" by brute force attack
                    DateTime lastAttempt = (DateTime)Application["_Last-Forms-Login-Attempt-" + Request.UserHostAddress];
                    if ((DateTime.Now - lastAttempt).TotalSeconds < 30)
                        throw new System.Security.SecurityException("You are trying to login too frequently, wait at least 30 seconds before you try again");
                }

                // Defaulting result of Active Event to unsuccessful
                e.Args.Value = false;

                // Retrieving supplied credentials
                string username = e.Args.GetExChildValue<string> ("username", context);
                string password = e.Args.GetExChildValue<string> ("password", context);
                bool persist = e.Args.GetExChildValue ("persist", context, false);

                // Getting password file in Node format
                Node pwdFile = null;

                // Locking access to password file
                lock (_passwordFileLocker) {
                    pwdFile = GetPasswordFile(context);
                }

                // Checking for match for specified username
                Node userNode = pwdFile["users"][username];
                if (userNode == null)
                    throw new System.Security.SecurityException("Credentials not accepted");

                // Checking for match on password
                if (userNode["password"].Get<string> (context) != password)
                    throw new System.Security.SecurityException("Credentials not accepted");

                // Success, figuring out if user must change passwords, and creating our ticket
                string role = userNode["role"].Get<string>(context);
                Ticket = new ApplicationContext.ContextTicket(
                    username, 
                    role, 
                    false);
                e.Args.Value = true;

                // Removing last login attempt
                Application.Remove ("_Last-Forms-Login-Attempt-" + Request.UserHostAddress);

                // Associating newly created Ticket with Application Context, since user now possibly have extended rights
                _context.UpdateTicket (Ticket);

                // Checking if we should create persistent cookie on disc to remember username for given client
                // Notice, we do NOT allow root account to persist to cookie
                if (role != "root" && persist) {

                    // Caller wants to create persistent cookie to remember username/password
                    HttpCookie cookie = new HttpCookie("_p5_user");
                    cookie.Expires = DateTime.Now.AddDays(30);
                    cookie.HttpOnly = true;
                    string salt = userNode["salt"].Get<string>(context);
                    cookie.Value = username + " " + context.Raise ("p5.crypto.hash-string", new Node(string.Empty, salt + password)).Value;
                    Response.Cookies.Add(cookie);
                }
            }
            catch {
                Application["_Last-Forms-Login-Attempt-" + Request.UserHostAddress] = DateTime.Now;
                throw;
            }
        }

        /// <summary>
        ///     Logs out a user from the ApplicationContext
        /// </summary>
        /// <param name="context">Application Context</param>
        /// <param name="e">Active Event arguments</param>
        [ActiveEvent (Name = "logout", Protected = true)]
        private void logout (ApplicationContext context, ActiveEventArgs e)
        {
            // By destroying this session value, default user will be used in future
            Session.Remove ("_ApplicationContext.ContextTicket");
            HttpCookie cookie = Request.Cookies.Get("_p5_user");
            cookie.Expires = DateTime.Now.AddDays(-1);
            Response.Cookies.Add(cookie);
        }

        /// <summary>
        ///     Returns the currently logged in Context user
        /// </summary>
        /// <param name="context">Application Context</param>
        /// <param name="e">Active Event arguments</param>
        [ActiveEvent (Name = "user", Protected = true)]
        private void user (ApplicationContext context, ActiveEventArgs e)
        {
            e.Args.Add("username", Ticket.Username);
            e.Args.Add("role", Ticket.Role);
            e.Args.Add("default", Ticket.IsDefault);
        }

        /// <summary>
        ///     Returns all roles in system
        /// </summary>
        /// <param name="context">Application Context</param>
        /// <param name="e">Active Event arguments</param>
        [ActiveEvent (Name = "roles", Protected = true)]
        private void roles (ApplicationContext context, ActiveEventArgs e)
        {
            // Getting password file in Node format, such that we can traverse file for all roles
            Node pwdFile = null;

            // Locking access to password file
            lock (_passwordFileLocker) {
                pwdFile = GetPasswordFile(context);
            }

            foreach (var idxUserNode in pwdFile["users"].Children) {
                if (e.Args.Children.FirstOrDefault(ix => ix.Name == idxUserNode["role"].Get<string>(context)) == null) {
                    e.Args.Add(idxUserNode["role"].Get<string>(context));
                }
            }

            // Making sure default role is added
            var configuration = ConfigurationManager.GetSection ("activeEventAssemblies") as ActiveEventAssemblies;
            if (!string.IsNullOrEmpty(configuration.DefaultContextRole)) {
                if (e.Args.Children.FirstOrDefault(ix => ix.Name == configuration.DefaultContextRole) == null) {
                    e.Args.Add(configuration.DefaultContextRole);
                }
            }
        }

        /// <summary>
        ///     Sends the given JavaScript to client once
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "send-javascript")]
        private void send_javascript (ApplicationContext context, ActiveEventArgs e)
        {
            // Looping through each JavaScript snippet
            foreach (var idxSnippet in XUtil.Iterate<string> (e.Args, context)) {

                // Passing file to client
                Manager.SendJavaScriptToClient (idxSnippet);
            }
        }
        
        /// <summary>
        ///     Includes the given JavaScript on page persistently
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "include-javascript")]
        private void include_javascript (ApplicationContext context, ActiveEventArgs e)
        {
            // Looping through each JavaScript snippet
            foreach (var idxSnippet in XUtil.Iterate<string> (e.Args, context)) {

                // Passing file to client
                RegisterJavaScript (idxSnippet);
            }
        }

        /// <summary>
        ///     Includes JavaScript file(s) persistently
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "include-javascript-file")]
        private void include_javascript_file (ApplicationContext context, ActiveEventArgs e)
        {
            // Looping through each JavaScript file
            foreach (var idxFile in XUtil.Iterate<string> (e.Args, context)) {

                // Passing file to client
                RegisterJavaScriptFile (idxFile);
            }
        }

        /// <summary>
        ///     Includes CSS StyleSheet file(s) persistently on the client side.
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "include-stylesheet-file")]
        private void include_stylesheet_file (ApplicationContext context, ActiveEventArgs e)
        {
            // Looping through each stylesheet file given
            foreach (var idxFile in XUtil.Iterate<string> (e.Args, context)) {

                // Register file for inclusion back to client
                RegisterStylesheetFile (idxFile);
            }
        }

        /// <summary>
        ///     Changes the title of your web page.
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "set-title")]
        private void set_title (ApplicationContext context, ActiveEventArgs e)
        {
            var title = XUtil.Single<string>(e.Args, context);
            if (Manager.IsPhosphorusRequest) {

                // passing title to client as JavaScript update
                Manager.SendJavaScriptToClient (string.Format ("document.title='{0}';", title.Replace ("\\", "\\\\").Replace ("'", "\\'")));
            }
            Title = title;

            // Storing Title in ViewState such that we can retrieve correct title later
            ViewState["P5-Title"] = Title;
        }

        /// <summary>
        ///     Returns the title of your web page.
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "get-title")]
        private void get_title (ApplicationContext context, ActiveEventArgs e)
        {
            // ViewState title has presedence, since it might have been changed, 
            // and "Title" property of page is not serialized into ViewState
            e.Args.Value = ViewState ["P5-Title"] ?? Title;
        }

        /// <summary>
        ///     Changes the URL/location of your web page.
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "set-location")]
        private void set_location (ApplicationContext context, ActiveEventArgs e)
        {
            if (Manager.IsPhosphorusRequest) {

                // Redirecting using JavaScript
                Manager.SendJavaScriptToClient (string.Format ("window.location='{0}';", XUtil.Single<string> (e.Args, context).Replace ("\\", "\\\\").Replace ("'", "\\'")));
            } else {

                // Redirecting using Response object
                Page.Response.Redirect (XUtil.Single<string> (e.Args, context));
            }
        }

        /// <summary>
        ///     Reloads the current document
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "reload-location")]
        private void reload_location (ApplicationContext context, ActiveEventArgs e)
        {
            // Redirecting using JavaScript
            Manager.SendJavaScriptToClient (string.Format ("window.location.replace(window.location.pathname);"));
        }

        /// <summary>
        ///     Returns the URL/location of your web page.
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "get-location")]
        private void get_location (ApplicationContext context, ActiveEventArgs e)
        {
            e.Args.Value = Request.Url.ToString ();
        }

        /// <summary>
        ///     Returns the given Node back to client as JSON.
        /// </summary>
        /// <param name="context">Application context Active Event is raised within</param>
        /// <param name="e">Parameters passed into Active Event</param>
        [ActiveEvent (Name = "return-value")]
        private void return_value (ApplicationContext context, ActiveEventArgs e)
        {
            var key = XUtil.Single<string> (e.Args, context);
            var str = p5.core.Utilities.Convert<string> (XUtil.SourceSingle(e.Args, context), context, "");
            Manager.SendObject (key, str);
        }

        #endregion

        #region [ -- "Private" helper Active Events -- ]

        /*
         * Invoked during installation. Sets root password, but only if existing password is null!
         */
        [ActiveEvent (Name = "p5.web.set-root-password")]
        private void p5_web_set_root_password (ApplicationContext context, ActiveEventArgs e)
        {
            // Retrieving password given
            string password = e.Args.GetExChildValue<string>("password", context);
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("You cannot set the root password to empty");

            // Retrieving password file, and making sure existing root password is null!
            Node rootPwdNode = null;

            // Locking access to password file
            lock (_passwordFileLocker) {
                rootPwdNode = GetPasswordFile(context)["users"]["root"];
            }

            if (rootPwdNode["password"].Value != null)
                throw new System.Security.SecurityException("Somebody tried to use installation Active event [p5.web.set-root-password] to change password of root account");

            // Logging in root user now, before changing password
            Ticket = new ApplicationContext.ContextTicket("root", "root", false);
            _context.UpdateTicket(Ticket);
            ChangePassword(context, password);
        }

        /*
         * Invoked during installation. Returns true if root password is null (server needs setup)
         */
        [ActiveEvent (Name = "p5.web.root-password-is-null", Protected = true)]
        private void p5_web_root_password_is_null (ApplicationContext context, ActiveEventArgs e)
        {
            // Retrieving password file, and making sure existing root password is null!
            Node rootPwdNode = null;

            // Locking access to password file
            lock (_passwordFileLocker) {
                rootPwdNode = GetPasswordFile(context)["users"]["root"];
            }

            e.Args.Value = rootPwdNode["password"].Value == null;
        }

        /*
         * Invoked by p5.web during creation of Widgets
         */
        [ActiveEvent (Name = "_p5.web.add-widget-ajax-event")]
        private void _p5_web_add_widget_ajax_event (ApplicationContext context, ActiveEventArgs e)
        {
            // Retrieving widget
            var widget = e.Args.Get<pf.Widget> (context);
            e.Args.Value = null; // dropping the actual Widget, to avoid serializing it into ViewState!

            // Mapping the widget's ajax event to our common event handler on page
            // But intentionally dropping [oninit], since it's a purely server-side lambda event, and not supposed to be pushed to client
            if (e.Args.Name != "oninit")
                widget [e.Args.Name] = "common_event_handler";

            // Storing our Widget Ajax event in storage
            WidgetAjaxEventStorage [widget.ID, e.Args.Name] = e.Args;
        }
        
        /*
         * Invoked by p5.web during creation of Widgets
         */
        [ActiveEvent (Name = "_p5.web.add-widget-lambda-event")]
        private void _p5_web_add_widget_lambda_event (ApplicationContext context, ActiveEventArgs e)
        {
            // Retrieving widget
            var widget = e.Args.Get<pf.Widget> (context);
            e.Args.Value = null; // dropping the actual Widget, to avoid serializing it into ViewState!

            // Verifying Active Event is not protected
            if (EventIsProtected(context, e.Args.Name))
                throw new ApplicationException(string.Format ("You cannot override Active Event '{0}' since it is protected", e.Args.Name));

            WidgetLambdaEventStorage [e.Args.Name, widget.ID] = e.Args;
        }

        #endregion

        #region [ -- Private helper methods -- ]

        /*
         * Changes password of the currently logged in Context user account
         */
        private void ChangePassword (ApplicationContext context, string newPwd)
        {
            var configuration = ConfigurationManager.GetSection ("activeEventAssemblies") as ActiveEventAssemblies;
            string rootFolder = context.Raise("p5.core.application-folder").Get<string>(context);

            // Locking access to password file
            lock (_passwordFileLocker) {

                Node pwdFile = GetPasswordFile(context);
                pwdFile["users"][context.Ticket.Username]["password"].Value = newPwd;
                string pwdFilePath = configuration.PasswordFile.Replace("~/", rootFolder);

                using (TextWriter writer = File.CreateText(pwdFilePath)) {
                    Node lambdaNode = new Node();
                    lambdaNode.AddRange(pwdFile.Children);
                    writer.Write(context.Raise ("lambda2lisp", lambdaNode).Get<string> (context));
                }
            }
        }

        /*
         * Helper to retrieve "_passwords" file
         */
        private static Node GetPasswordFile (ApplicationContext context)
        {
            // Getting filepath to pwd file
            var configuration = ConfigurationManager.GetSection ("activeEventAssemblies") as ActiveEventAssemblies;
            string rootFolder = context.Raise("p5.core.application-folder").Get<string>(context);
            string pwdFilePath = configuration.PasswordFile.Replace("~", rootFolder);

            // Checking file exist
            if (!File.Exists(pwdFilePath))
                CreateDefaultPasswordFile (context, pwdFilePath);

            // Reading up passwords file
            using (TextReader reader = new StreamReader(File.OpenRead(pwdFilePath))) {

                // Returning file as lambda
                string users = reader.ReadToEnd();
                Node usersNode = context.Raise("lisp2lambda", new Node(string.Empty, users));
                return usersNode;
            }
        }

        /*
         * Creates a default "_passwords" file, and a default "root" user
         */
        private static void CreateDefaultPasswordFile (ApplicationContext context, string pwdFile)
        {
            // Checking if ".passwords" file exist, and if not, creating a default
            using (TextWriter writer = File.CreateText(pwdFile)) {

                // Creating default root password, salt unique to user, and writing to file
                var salt = "";
                for (var idxRndNo = 0; idxRndNo < new Random (DateTime.Now.Millisecond).Next (1,5); idxRndNo++) {
                    salt += Guid.NewGuid().ToString();
                }
                salt = salt.Replace("-", "");
                writer.WriteLine(@"users");
                writer.WriteLine(@"  root");
                writer.WriteLine(@"    salt:" + salt);
                writer.WriteLine(@"    password");
                writer.WriteLine(@"    role:root");
            }
        }

        /*
         * Will try to login from persistent cookie
         */
        private void TryLoginFromPersistentCookie()
        {
            try {

                // Checking if client has persistent cookie
                HttpCookie cookie = Request.Cookies.Get("_p5_user");
                if (cookie != null) {

                    if (Application["_Last-Cookie-Login-Attempt-" + Request.UserHostAddress] != null) {

                        // User has been trying to login with cookie previously, which is possibly an intrusion attempt
                        DateTime lastAttempt = (DateTime)Application["_Last-Cookie-Login-Attempt-" + Request.UserHostAddress];
                        if ((DateTime.Now - lastAttempt).TotalSeconds < 30)
                            throw new System.Security.SecurityException ("You have tried to login to this system to rapidly, wait 30 seconds before you try again.");
                    }

                    // User has persistent cookie associated with client
                    var cookieSplits = cookie.Value.Split (' ');
                    if (cookieSplits.Length != 2)
                        throw new System.Security.SecurityException ("Cookie not accepted");

                    string cookieUsername = cookieSplits[0];
                    string cookieHashSaltedPwd = cookieSplits[1];
                    Node pwdFile = null;

                    // Locking access to password file
                    lock (_passwordFileLocker) {
                        pwdFile = GetPasswordFile(_context);
                    }

                    Node userNode = pwdFile["users"][cookieUsername];
                    if (userNode == null)
                        throw new System.Security.SecurityException ("Cookie not accepted");

                    // User exists, retrieving salt and password to see if we have a match
                    string salt = userNode["salt"].Get<string> (_context);
                    string password = userNode["password"].Get<string> (_context);
                    string hashSaltedPwd = _context.Raise("p5.crypto.hash-string", new Node(string.Empty, salt + password)).Get<string>(_context);
                    if (hashSaltedPwd != cookieHashSaltedPwd)
                        throw new System.Security.SecurityException ("Cookie not accepted");

                    // MATCH, discarding previous ticket and Context to create a new
                    Ticket = new ApplicationContext.ContextTicket(
                        userNode.Name, 
                        userNode ["role"].Get<string>(_context), 
                        false);
                    _context = Loader.Instance.CreateApplicationContext(Ticket);
                    Application.Remove ("_Last-Cookie-Login-Attempt-" + Request.UserHostAddress);
                }
            }
            catch {
                Application["_Last-Cookie-Login-Attempt-" + Request.UserHostAddress] = DateTime.Now;
                HttpCookie cookie = Request.Cookies.Get("_p5_user");
                cookie.Expires = DateTime.Now.AddDays(-1);
                Response.Cookies.Add(cookie);
                throw;
            }
        }

        /*
         * Helper to retrieve a list of widgets from a Node
         */
        private IEnumerable<T> FindWidgets<T> (Node args) where T : Control
        {
            foreach (var widget in XUtil.Iterate<string> (args, _context).Select (ix => FindControl<T>(ix, Page))) {

                if (widget == null)
                    continue;
                yield return widget;
            }
        }
        
        /*
         * Helper to retrieve a list of widgets from a Node
         */
        private IEnumerable<T> FindWidgetsThrow<T> (Node args, string activeEventName) where T : Control
        {
            foreach (var ctrl in XUtil.Iterate<string> (args, _context).Select (ix => FindControl<Control>(ix, Page))) {

                if (ctrl == null)
                    throw new ArgumentException("Couldn't find control with that ID");
                var widget = ctrl as T;
                if (widget == null)
                    throw new ArgumentException(string.Format("You cannot use [{0}] on a Control that is not a P5.ajax Widget. [{0}] was invoked for '{1}', which is of type '{2}'", activeEventName, ctrl.ID, ctrl.GetType().FullName));
                yield return widget;
            }
        }
        
        /*
         * Helper to retrieve a list of widgets from a Node
         */
        private IEnumerable<T> FindWidgetsBy<T> (Node args, Widget idx, ApplicationContext context) where T : Widget
        {
            if (idx == null)
                yield break;

            bool match = true;
            foreach (var idxNode in args.Children) {
                if (!idx.HasAttribute(idxNode.Name) || idx[idxNode.Name] != idxNode.GetExValue<string>(context, null)) {
                    match = false;
                    break;
                }
            }
            if (match)
                yield return idx as T;
            foreach (var idxChild in idx.Controls) {
                foreach (var idxSubFind in FindWidgetsBy<T> (args, idxChild as T, context)) {
                    yield return idxSubFind;
                }
            }
        }

        /*
         * recursively traverses entire Control hierarchy on page, and adds up into result node
         */
        private static void ListWidgets (List<string> filter, Node args, Control current)
        {
            if (filter.Count == 0 || filter.Count(ix => ix == current.ID) > 0) {

                // Current Control is matching one of our filters, or there are no filters
                string typeString = current.GetType().FullName;
                if (current is Widget)
                    typeString = typeString.Substring(typeString.LastIndexOf(".") + 1).ToLower();

                args.Add(typeString, current.ID);
            }
            foreach (Control idxChild in current.Controls) {
                ListWidgets (filter, args, idxChild);
            }
        }

        /*
         * Common helper method for creating widgets
         */
        private void CreateWidget (ApplicationContext context, Node args, string type)
        {
            // finding parent widget first, which defaults to "main container" widget, if no parent is given
            var parent = FindControl<pf.Widget>(args.GetChildValue ("parent", context, "cnt"), Page);

            // creating our widget by raising the active event responsible for creating it
            var createNode = args.Clone ();
            if (createNode.Value == null) {

                // Creating a unique ID for widget BEFORE we create Widget, since we need it to create our Widget Events 
                // before we create widget itself, since [oninit] might depend upon widget events, and oninit 
                // is raised during creation process, before it returns.
                createNode.Value = Container.CreateUniqueId ();
            }

            // ORDER COUNTS!! Read above comments!

            // Initializing Active Events for widget, if there are any given BEFORE widget is created, 
            // since [oninit] might depend upon Widget events for widget
            var eventNode = createNode.Find (idx => idx.Name == "events");
            if (eventNode != null)
                CreateWidgetLambdaEvents (createNode.Get<string> (context), eventNode.UnTie (), context);

            createNode.Insert (0, new Node ("__parent", parent));
            context.Raise ("p5.web.widgets." + type, createNode);
        }

        /*
         * helper for [get-widget-property], creates a return value for one property
         */
        private static void CreatePropertyReturn (Node node, Node nameNode, Widget widget, object value = null)
        {
            // checking if widget has the attribute, if it doesn't, we don't even add any return nodes at all, to make it possible
            // to separate widgets which has the property, but no value, (such as the selected property on checkboxes for instance),
            // and widgets that does not have the property at all
            if (value == null && !widget.HasAttribute (nameNode.Name))
                return;

            node.FindOrCreate (widget.ID).Add (nameNode.Name).LastChild.Value = value == null ? widget [nameNode.Name] : value;
        }

        /*
         * recursively searches through page for Container with specified id, starting from "idx"
         */
        private T FindControl<T> (string id, Control idx) where T : Control
        {
            if (idx.ID == id)
                return idx as T;
            return (from Control idxChild in idx.Controls select FindControl<T> (id, idxChild)).FirstOrDefault (tmpRet => tmpRet != null);
        }

        /*
         * creates local widget events for web widgets created through [create-widget]
         */
        private void CreateWidgetLambdaEvents (string widgetId, Node eventNode, ApplicationContext context)
        {
            // Looping through each event in args
            foreach (var idxEvt in eventNode.Children.ToList ()) {

                // Verifying Active Event is not protected
                if (EventIsProtected (context, idxEvt.Name))
                    throw new ApplicationException(string.Format ("You cannot override Active Event '{0}' since it is protected", idxEvt.Name));

                // Adding lambda event to lambda event storage
                WidgetLambdaEventStorage [idxEvt.Name, widgetId] = idxEvt;
            }
        }

        /*
         * Helper to figure out if Active Event is protected or not
         */
        private Node _protectedDynamicEvents = null;
        private bool EventIsProtected (ApplicationContext context, string evt)
        {
            // Verifying Active Event is not protected
            if (context.IsProtected(evt))
                return true;
            if (_protectedDynamicEvents == null) {
                _protectedDynamicEvents = context.Raise("_p5.lambda.get-protected-events");
            }
            if (_protectedDynamicEvents[evt] != null)
                return true;
            return false;
        }

        /*
         * Clears all lambda Active Events for widget
         */
        private void RemoveWidgetLambdaEvents (Control widget)
        {
            // Removing widget entirely from WidgetEventStorage
            WidgetLambdaEventStorage.RemoveFromKey2(widget.ID);

            // looping through all child widgets, and removing those, by recursively calling self
            foreach (Control idxChild in widget.Controls) {
                RemoveWidgetLambdaEvents (idxChild);
            }
        }

        /*
         * recursively removes all events for control and all of its children controls
         */
        private void RemoveWidgetAjaxEvents (Control idx)
        {
            // Removing all Ajax Events for widget
            WidgetAjaxEventStorage.RemoveFromKey1(idx.ID);

            // recursively removing all ajax events for all of control's children controls
            foreach (Control idxChild in idx.Controls) {
                RemoveWidgetAjaxEvents (idxChild);
            }
        }
        
        /*
         * Removing all events for widget recursively
         */
        private void RemoveAllEventsRecursive (Control widget)
        {
            if (widget is Widget) {

                // Removing all Ajax Events for widget
                WidgetAjaxEventStorage.RemoveFromKey1(widget.ID);

                // Removing all lambda events for widget
                WidgetLambdaEventStorage.RemoveFromKey2(widget.ID);
            }

            // Recursively invoking for "self"
            foreach (Widget idx in widget.Controls) {

                RemoveAllEventsRecursive(idx);
            }
        }

        #endregion
    }
}
