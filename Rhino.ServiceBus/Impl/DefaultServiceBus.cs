using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Castle.MicroKernel;
using Castle.MicroKernel.Proxy;
using log4net;
using Rhino.ServiceBus.Exceptions;
using Rhino.ServiceBus.Internal;
using Rhino.ServiceBus.MessageModules;
using Rhino.ServiceBus.Messages;
using Rhino.ServiceBus.Sagas;

namespace Rhino.ServiceBus.Impl
{
	using Queues.Utils;

	public class DefaultServiceBus : IStartableServiceBus
    {
        private readonly IKernel kernel;

        private readonly ILog logger = LogManager.GetLogger(typeof(DefaultServiceBus));
        private readonly IMessageModule[] modules;
        private readonly IReflection reflection;
        private readonly ISubscriptionStorage subscriptionStorage;
        private readonly ITransport transport;
        private readonly MessageOwnersSelector messageOwners;
    	[ThreadStatic] public static object currentMessage;
        private readonly IEndpointRouter endpointRouter;

        public DefaultServiceBus(
            IKernel kernel,
            ITransport transport,
            ISubscriptionStorage subscriptionStorage,
            IReflection reflection,
            IMessageModule[] modules,
            MessageOwner[] messageOwners, 
            IEndpointRouter endpointRouter)
        {
            this.transport = transport;
            this.endpointRouter = endpointRouter;
            this.messageOwners = new MessageOwnersSelector(messageOwners, endpointRouter);
            this.subscriptionStorage = subscriptionStorage;
            this.reflection = reflection;
            this.modules = modules;
            this.kernel = kernel;
        }

		
        public IMessageModule[] Modules
        {
            get { return modules; }
        }

        #region IStartableServiceBus Members

        public event Action<Reroute> ReroutedEndpoint;

        public void Publish(params object[] messages)
        {
            if (PublishInternal(messages) == false)
                throw new MessagePublicationException("There were no subscribers for (" +
                                                      messages.First() + ")"
                    );
        }

        public void Notify(params object[] messages)
        {
            PublishInternal(messages);
        }

        public void Reply(params object[] messages)
        {
            if (messages == null)
                throw new ArgumentNullException("messages");

            if (messages.Length == 0)
                throw new MessagePublicationException("Cannot reply with an empty message batch");

            transport.Reply(messages);
        }

        public void Send(Endpoint endpoint, params object[] messages)
        {
            if (messages == null)
                throw new ArgumentNullException("messages");

            if (messages.Length == 0)
                throw new MessagePublicationException("Cannot send empty message batch");

            transport.Send(endpoint, messages);
        }

        public void Send(params object[] messages)
        {
            Send(messageOwners.GetEndpointForMessageBatch(messages), messages);
        }

        

        public IDisposable AddInstanceSubscription(IMessageConsumer consumer)
        {
            var information = new InstanceSubscriptionInformation
            {
                Consumer = consumer,
                InstanceSubscriptionKey = Guid.NewGuid(),
                ConsumedMessages = reflection.GetMessagesConsumed(consumer),
            };
            subscriptionStorage.AddLocalInstanceSubscription(consumer);
            SubscribeInstanceSubscription(information);
            
            return new DisposableAction(() =>
            {
                subscriptionStorage.RemoveLocalInstanceSubscription(information.Consumer);
                UnsubscribeInstanceSubscription(information);
                information.Dispose();
            });
        }

        public Endpoint Endpoint
        {
            get { return transport.Endpoint; }
        }

	    public CurrentMessageInformation CurrentMessageInformation
	    {
	        get { return transport.CurrentMessageInformation; }
	    }

        public void Dispose()
        {
			FireServiceBusAware(aware => aware.BusDisposing(this));
            transport.Dispose();
            transport.MessageArrived -= Transport_OnMessageArrived;

            foreach (IMessageModule module in modules)
            {
                module.Stop(transport);
            }

            var subscriptionAsModule = subscriptionStorage as IMessageModule;
            if (subscriptionAsModule != null)
                subscriptionAsModule.Stop(transport);
        	FireServiceBusAware(aware => aware.BusDisposed(this));
        }

        public void Start()
        {
        	FireServiceBusAware(aware => aware.BusStarting(this));
            logger.DebugFormat("Starting the bus for {0}", Endpoint);

            var subscriptionAsModule = subscriptionStorage as IMessageModule;
            if (subscriptionAsModule != null)
            {
                logger.DebugFormat("Initating subscription storage as message module: {0}", subscriptionAsModule);
                subscriptionAsModule.Init(transport);
            }
            foreach (var module in modules)
            {
                logger.DebugFormat("Initating message module: {0}", module);
                module.Init(transport);
            }
            transport.MessageArrived += Transport_OnMessageArrived;

            transport.AdministrativeMessageArrived += Transport_OnAdministrativeMessageArrived;

            transport.Start();
            subscriptionStorage.Initialize();

            AutomaticallySubscribeConsumerMessages();

        	FireServiceBusAware(aware => aware.BusStarted(this));
        }

        private bool Transport_OnAdministrativeMessageArrived(CurrentMessageInformation info)
        {
            var route = info.Message as Reroute;

            if (route == null)
                return false;

            endpointRouter.RemapEndpoint(route.OriginalEndPoint, route.NewEndPoint);
            var reroutedEndpoint = ReroutedEndpoint;
            if(reroutedEndpoint!=null)
                reroutedEndpoint(route);
            
            return true;
        }

        private void FireServiceBusAware(Action<IServiceBusAware> action)
    	{
    		foreach(var aware in kernel.ResolveAll<IServiceBusAware>())
    		{
    			action(aware);
    		}
    	}

    	public void Subscribe(Type type)
        {
            foreach (var owner in messageOwners.Of(type))
            {
                logger.InfoFormat("Subscribing {0} on {1}", type.FullName, owner.Endpoint);

            	var endpoint = endpointRouter.GetRoutedEndpoint(owner.Endpoint);
            	endpoint.Transactional = owner.Transactional;
            	Send(endpoint, new AddSubscription
                {
                    Endpoint = Endpoint,
                    Type = type.FullName
                });
            }
        }

        public void Subscribe<T>()
        {
            Subscribe(typeof(T));
        }

        public void Unsubscribe<T>()
        {
            Unsubscribe(typeof(T));
        }

        public void Unsubscribe(Type type)
        {
            foreach (var owner in messageOwners.Of(type))
            {
            	var endpoint = endpointRouter.GetRoutedEndpoint(owner.Endpoint);
            	endpoint.Transactional = owner.Transactional;
            	Send(endpoint, new RemoveSubscription
                {
                    Endpoint = Endpoint,
                    Type = type.FullName
                });
            }
        }

        private void SubscribeInstanceSubscription(InstanceSubscriptionInformation information)
        {
            foreach (var message in information.ConsumedMessages)
            {
                bool subscribed = false;
                foreach (var owner in messageOwners.Of(message))
                {
                    logger.DebugFormat("Instance subscribition for {0} on {1}",
                        message.FullName,
                        owner.Endpoint);

                    subscribed = true;
                	var endpoint = endpointRouter.GetRoutedEndpoint(owner.Endpoint);
                	endpoint.Transactional = owner.Transactional;
                	Send(endpoint, new AddInstanceSubscription
                    {
                        Endpoint = Endpoint.Uri.ToString(),
                        Type = message.FullName,
                        InstanceSubscriptionKey = information.InstanceSubscriptionKey
                    });
                }
                if(subscribed ==false)
                    throw new SubscriptionException("Could not find any owner for message " + message +
                                                    " that we could subscribe for");
            }
        }

        public void UnsubscribeInstanceSubscription(InstanceSubscriptionInformation information)
        {
            foreach (var message in information.ConsumedMessages)
            {
                foreach (var owner in messageOwners.NotOf(message))
                {
                	var endpoint = endpointRouter.GetRoutedEndpoint(owner.Endpoint);
                	endpoint.Transactional = owner.Transactional;
                	Send(endpoint, new RemoveInstanceSubscription
                    {
                        Endpoint = Endpoint.Uri.ToString(),
                        Type = message.FullName,
                        InstanceSubscriptionKey = information.InstanceSubscriptionKey
                    });
                }
            }
        }

        /// <summary>
    	/// Handles the current message later.
    	/// </summary>
    	public void HandleCurrentMessageLater()
    	{
            transport.Send(Endpoint, DateTime.Now, new[] { currentMessage });
    	}

        /// <summary>
    	/// Send the message with a built in delay in its processing
    	/// </summary>
    	/// <param name="endpoint">The endpoint.</param>
    	/// <param name="time">The time.</param>
    	/// <param name="msgs">The messages.</param>
    	public void DelaySend(Endpoint endpoint, DateTime time, params object[] msgs)
    	{
    		transport.Send(endpoint, time, msgs);
    	}

        /// <summary>
        /// Send the message with a built in delay in its processing
        /// </summary>
        /// <param name="time">The time.</param>
        /// <param name="msgs">The messages.</param>
        public void DelaySend(DateTime time, params object[] msgs)
        {
            DelaySend(messageOwners.GetEndpointForMessageBatch(msgs), time, msgs);
        }
        
        private void AutomaticallySubscribeConsumerMessages()
        {
            var handlers = kernel.GetAssignableHandlers(typeof(IMessageConsumer));
            foreach (var handler in handlers)
            {
                var msgs = reflection.GetMessagesConsumed(handler.ComponentModel.Implementation,
                                                          type => type == typeof(OccasionalConsumerOf<>)
														  || type == typeof(Consumer<>.SkipAutomaticSubscription));
                foreach (var msg in msgs)
                {
                    Subscribe(msg);
                }
            }
        }

        #endregion

        private bool PublishInternal(object[] messages)
        {
            if (messages == null)
                throw new ArgumentNullException("messages");

            bool sentMsg = false;
            if (messages.Length == 0)
                throw new MessagePublicationException("Cannot publish an empty message batch");
            object msg = messages[0];
            IEnumerable<Uri> subscriptions = subscriptionStorage.GetSubscriptionsFor(msg.GetType());
            foreach (Uri subscription in subscriptions)
            {
                transport.Send(endpointRouter.GetRoutedEndpoint(subscription), messages);
                sentMsg = true;
            }
            return sentMsg;
        }

        public bool Transport_OnMessageArrived(CurrentMessageInformation msg)
        {
            object[] consumers = GatherConsumers(msg);
        	
			if (consumers.Length == 0)
            {
                logger.ErrorFormat("Got message {0}, but had no consumers for it", msg.Message);
                return false;
            }
            try
            {
				currentMessage = msg.Message;

                foreach (var consumer in consumers)
                {
                    logger.DebugFormat("Invoking consume on {0} for message {1}, from '{2}' to '{3}'",
                                       consumer,
                                       msg.Message,
                                       msg.Source,
                                       msg.Destination);
                    
                    var sp = Stopwatch.StartNew();
                    try
                    {
                        reflection.InvokeConsume(consumer, msg.Message);
                    }
                    catch (Exception e)
                    {
                        if(logger.IsDebugEnabled)
                        {
                            var message = string.Format("Consumer {0} failed to process message {1}",
                                                       consumer,
                                                       msg.Message
                                );
                            logger.Debug(message,e);
                        }
                        throw;
                    }
                    finally
                    {
                        sp.Stop();
                        var elapsed = sp.Elapsed;
                        logger.DebugFormat("Consumer {0} finished processing {1} in {2}", consumer, msg.Message, elapsed);
                    }
                    var sagaEntity = consumer as IAccessibleSaga;
                    if (sagaEntity == null)
                        continue;
                    PersistSagaInstance(sagaEntity);
                }
                return true;
            }
            finally
            {
            	currentMessage = null;

                foreach (var consumer in consumers)
                {
                    kernel.ReleaseComponent(consumer);
                }
            }
        }

        private void PersistSagaInstance(IAccessibleSaga saga)
        {
            Type persisterType = reflection.GetGenericTypeOf(typeof(ISagaPersister<>), saga);
            object persister = kernel.Resolve(persisterType);

            if (saga.IsCompleted)
                reflection.InvokeSagaPersisterComplete(persister, saga);
            else
                reflection.InvokeSagaPersisterSave(persister, saga);
        }

        public object[] GatherConsumers(CurrentMessageInformation msg)
        {
            object[] sagas = GetSagasFor(msg.Message);
            var sagaMessage = msg.Message as ISagaMessage;

            var msgType = msg.Message.GetType();
            object[] instanceConsumers = subscriptionStorage
                .GetInstanceSubscriptions(msgType);

            var consumerTypes = reflection.GetGenericTypesOfWithBaseTypes(typeof(ConsumerOf<>), msg.Message);
            var occasionalConsumerTypes = reflection.GetGenericTypesOfWithBaseTypes(typeof(OccasionalConsumerOf<>), msg.Message);
            var consumers = GetAllNonOccasionalConsumers(consumerTypes, occasionalConsumerTypes, sagas);
            for (var i = 0; i < consumers.Length; i++)
            {
                var saga = consumers[i] as IAccessibleSaga;
                if (saga == null)
                    continue;

                // if there is an existing saga, we skip the new one
                var type = saga.GetType();
                if (sagas.Any(type.IsInstanceOfType))
                {
                    kernel.ReleaseComponent(consumers[i]);
                    consumers[i] = null;
                    continue;
                }
                // we do not create new sagas if the saga is not initiated by
                // the message
                var initiatedBy = reflection.GetGenericTypeOf(typeof(InitiatedBy<>),msgType);
                if(initiatedBy.IsInstanceOfType(saga)==false)
                {
                    kernel.ReleaseComponent(consumers[i]);
                    consumers[i] = null;
                    continue;
                }

                saga.Id = sagaMessage != null ?
                    sagaMessage.CorrelationId :
                    GuidCombGenerator.Generate();
            }
            return instanceConsumers
                .Union(sagas)
                .Union(consumers.Where(x => x != null))
                .ToArray();
        }

        /// <summary>
        /// Here we don't use ResolveAll from Windsor because we want to get an error
        /// if a component exists which isn't valid
        /// </summary>
        private object[] GetAllNonOccasionalConsumers(ICollection<Type> consumerTypes, ICollection<Type> occasionalConsumerTypes, IEnumerable<object> instanceOfTypesToSkipResolving)
        {
            var allHandlers = new List<IHandler>();
            foreach (var consumerType in consumerTypes)
            {
                var handlers = kernel.GetAssignableHandlers(consumerType);
                allHandlers.AddRange(handlers);
            }

            var consumers = new List<object>(allHandlers.Count);
            foreach (var handler in allHandlers)
            {
                var implementation = handler.ComponentModel.Implementation;
                var occasionalConsumerFound = false;
                foreach (var occasionalConsumerType in occasionalConsumerTypes)
                {
                    if (occasionalConsumerType.IsAssignableFrom(implementation))
                    {
                        occasionalConsumerFound = true;
                        break;
                    }
                }
                if (occasionalConsumerFound)
                    continue;
                
                if (instanceOfTypesToSkipResolving.Any(x => x.GetType() == implementation))
                    continue;

                consumers.Add(handler.Resolve(CreationContext.Empty));
            }
            return consumers.ToArray();
        }

        private object[] GetSagasFor(object message)
        {
            var instances = new List<object>();

            Type orchestratesType = reflection.GetGenericTypeOf(typeof(Orchestrates<>), message);
            Type initiatedByType = reflection.GetGenericTypeOf(typeof(InitiatedBy<>), message);

            var handlers = kernel.GetAssignableHandlers(orchestratesType)
                                                   .Union(kernel.GetAssignableHandlers(initiatedByType));

            foreach (IHandler sagaHandler in handlers)
            {
                Type sagaType = sagaHandler.ComponentModel.Implementation;
                
                //first try to execute any saga finders.
                Type sagaFinderType = reflection.GetGenericTypeOf(typeof (ISagaFinder<,>), sagaType, ProxyUtil.GetUnproxiedType(message));
                var sagaFinderHandlers = kernel.GetAssignableHandlers(sagaFinderType);
                foreach (var sagaFinderHandler in sagaFinderHandlers)
                {
                    try
                    {
                        var sagaFinder = kernel.Resolve(sagaFinderHandler.Service);
                        var saga = reflection.InvokeSagaFinderFindBy(sagaFinder, message);
                        if (saga != null)
                            instances.Add(saga);
                    }
                    finally
                    {
                        kernel.ReleaseComponent(sagaFinderHandler);
                    }
                }

                //we will try to use an ISagaMessage's Correlation id next.
                var sagaMessage = message as ISagaMessage;
                if (sagaMessage == null)
                    continue;

                Type sagaPersisterType = reflection.GetGenericTypeOf(typeof(ISagaPersister<>),
                                                                     sagaType);

                object sagaPersister = kernel.Resolve(sagaPersisterType);
                try
                {
                    object sagas = reflection.InvokeSagaPersisterGet(sagaPersister, sagaMessage.CorrelationId);
                    if (sagas == null)
                        continue;
                    instances.Add(sagas);
                }
                finally
                {
                    kernel.ReleaseComponent(sagaPersister);
                }
            }
            return instances.ToArray();
        }
    }
}
