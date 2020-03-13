﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using UnityEngine;
using TouhouCardEngine.Interfaces;

namespace TouhouCardEngine
{
    public class TriggerManager : MonoBehaviour, ITriggerManager
    {
        [Serializable]
        public class EventListItem
        {
            [SerializeField]
            string _eventName;
            public string eventName
            {
                get { return _eventName; }
            }
            [SerializeField]
            List<TriggerListItem> _triggerList = new List<TriggerListItem>();
            public List<TriggerListItem> triggerList
            {
                get { return _triggerList; }
            }
            public EventListItem(string eventName)
            {
                _eventName = eventName;
            }
        }
        [Serializable]
        public class TriggerListItem
        {
            public ITrigger trigger { get; }
            public TriggerListItem(ITrigger trigger)
            {
                this.trigger = trigger;
            }
        }
        [SerializeField]
        List<EventListItem> _eventList = new List<EventListItem>();
        /// <summary>
        /// 注册触发器
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="trigger"></param>
        public void register(string eventName, ITrigger trigger)
        {
            EventListItem eventItem = _eventList.FirstOrDefault(ei => ei.eventName == eventName);
            if (eventItem == null)
            {
                eventItem = new EventListItem(eventName);
                _eventList.Add(eventItem);
            }
            if (!eventItem.triggerList.Any(ti => ti.trigger == trigger))
            {
                TriggerListItem item = new TriggerListItem(trigger);
                eventItem.triggerList.Add(item);
                if (currentEvent != null)
                {
                    EventListItem insertEventItem = _insertEventList.FirstOrDefault(ei => ei.eventName == eventName);
                    if (insertEventItem == null)
                    {
                        insertEventItem = new EventListItem(eventName);
                        _insertEventList.Add(insertEventItem);
                    }
                    insertEventItem.triggerList.Add(item);
                }
            }
            else
                throw new RepeatRegistrationException(eventName, trigger);
        }
        [SerializeField]
        List<EventListItem> _insertEventList = new List<EventListItem>();
        public bool remove(string eventName, ITrigger trigger)
        {
            EventListItem eventItem = _eventList.FirstOrDefault(ei => ei.eventName == eventName);
            if (eventItem == null)
                return false;
            else
                return eventItem.triggerList.RemoveAll(ti => ti.trigger == trigger) > 0;
        }
        public ITrigger[] getTriggers(string eventName)
        {
            EventListItem eventItem = _eventList.FirstOrDefault(ei => ei.eventName == eventName);
            if (eventItem == null)
                return new ITrigger[0];
            return eventItem.triggerList.Select(ti => ti.trigger).ToArray();
        }
        public IEventArg getEventArg(string[] eventNames, object[] args)
        {
            return new GeneratedEventArg(eventNames, args);
        }
        public string getName<T>() where T : IEventArg
        {
            return typeof(T).FullName;
        }
        public void register<T>(ITrigger<T> trigger) where T : IEventArg
        {
            register(getName<T>(), trigger);
        }
        public bool remove<T>(ITrigger<T> trigger) where T : IEventArg
        {
            return remove(getName<T>(), trigger);
        }
        public ITrigger<T>[] getTriggers<T>() where T : IEventArg
        {
            return getTriggers(getName<T>()).Where(t => t is ITrigger<T>).Cast<ITrigger<T>>().ToArray();
        }
        public string getNameBefore<T>() where T : IEventArg
        {
            return "Before" + getName<T>();
        }
        public string getNameAfter<T>() where T : IEventArg
        {
            return "After" + getName<T>();
        }
        public void registerBefore<T>(ITrigger<T> trigger) where T : IEventArg
        {
            register(getNameBefore<T>(), trigger);
        }
        public void registerAfter<T>(ITrigger<T> trigger) where T : IEventArg
        {
            register(getNameAfter<T>(), trigger);
        }
        public bool removeBefore<T>(ITrigger<T> trigger) where T : IEventArg
        {
            return remove(getNameBefore<T>(), trigger);
        }
        public bool removeAfter<T>(ITrigger<T> trigger) where T : IEventArg
        {
            return remove(getNameAfter<T>(), trigger);
        }
        public ITrigger<T>[] getTriggersBefore<T>() where T : IEventArg
        {
            return getTriggers(getNameBefore<T>()).Where(t => t is ITrigger<T>).Cast<ITrigger<T>>().ToArray();
        }
        public ITrigger<T>[] getTriggersAfter<T>() where T : IEventArg
        {
            return getTriggers(getNameAfter<T>()).Where(t => t is ITrigger<T>).Cast<ITrigger<T>>().ToArray();
        }
        public async Task doEvent<T>(string[] eventNames, T eventArg, params object[] args) where T : IEventArg
        {
            eventArg.afterNames = eventNames;
            eventArg.args = args;
            await doEvent(eventArg);
        }
        public async Task doEvent(string[] eventNames, object[] args)
        {
            await doEvent(getEventArg(eventNames, args));
        }
        public Task doEvent(string eventName, params object[] args)
        {
            return doEvent(new string[] { eventName }, args);
        }
        public async Task doEvent<T>(string[] beforeNames, string[] afterNames, T eventArg, Func<T, Task> action, params object[] args) where T : IEventArg
        {
            eventArg.beforeNames = beforeNames;
            eventArg.afterNames = afterNames;
            eventArg.args = args;
            await doEvent(eventArg, action);
        }
        public async Task doEvent<T>(T eventArg) where T : IEventArg
        {
            if (eventArg == null)
                throw new ArgumentNullException(nameof(eventArg));
            //加入事件链
            EventArgItem eventArgItem = new EventArgItem() { eventArg = eventArg };
            _eventChainList.Add(eventArgItem);
            //获取事件名
            IEnumerable<string> eventNames = eventArg.afterNames;
            if (eventNames == null)
                eventNames = new string[] { getName<T>() };
            else
                eventNames = eventNames.Concat(new string[] { getName<T>() });
            eventNames = eventNames.Distinct().ToArray();
            //对注册事件进行排序
            List<ITrigger> triggerList = new List<ITrigger>();
            foreach (string eventName in eventNames)
            {
                EventListItem eventItem = _eventList.FirstOrDefault(ei => ei.eventName == eventName);
                if (eventItem == null)
                    continue;
                eventItem.triggerList.Sort((a, b) => a.trigger.compare(b.trigger, eventArg));
                triggerList.AddRange(eventItem.triggerList.Select(ti => ti.trigger));
            }
            triggerList.Sort((a, b) => a.compare(b, eventArg));
            //执行注册事件
            while (triggerList.Count > 0)
            {
                ITrigger trigger = triggerList[0];
                triggerList.RemoveAt(0);
                if (trigger is ITrigger<T> triggerT)
                    await triggerT.invoke(eventArg);
                else
                    await trigger.invoke(eventArg);
                if (_insertEventList.Count > 0)
                {
                    foreach (string eventName in eventNames)
                    {
                        EventListItem insertEventItem = _insertEventList.FirstOrDefault(ei => ei.eventName == eventName);
                        if (insertEventItem == null)
                            continue;
                        if (insertEventItem.triggerList.Count > 0)
                        {
                            EventListItem eventItem = _eventList.FirstOrDefault(ei => ei.eventName == eventName);
                            if (eventItem != null)
                                eventItem.triggerList.Sort((a, b) => a.trigger.compare(b.trigger, eventArg));
                            triggerList.AddRange(insertEventItem.triggerList.Select(ti => ti.trigger));
                            triggerList.Sort((a, b) => a.compare(b, eventArg));
                            _insertEventList.Remove(insertEventItem);
                        }
                    }
                }
            }
            //移出事件链
            _eventChainList.Remove(eventArgItem);
        }
        public async Task doEvent<T>(T eventArg, Func<T, Task> action) where T : IEventArg
        {
            if (eventArg == null)
                throw new ArgumentNullException(nameof(eventArg));
            EventArgItem eventArgItem = new EventArgItem() { eventArg = eventArg };
            _eventChainList.Add(eventArgItem);
            eventArg.isCanceled = false;
            eventArg.repeatTime = 0;
            eventArg.action = arg =>
            {
                return action.Invoke((T)arg);
            };
            //Before
            IEnumerable<string> beforeNames = eventArg.beforeNames;
            if (beforeNames == null)
                beforeNames = new string[] { getNameBefore<T>() };
            else
                beforeNames = beforeNames.Concat(new string[] { getNameBefore<T>() });
            beforeNames = beforeNames.Distinct();
            List<ITrigger> triggerList = new List<ITrigger>();
            foreach (string beforeName in beforeNames)
            {
                EventListItem eventItem = _eventList.FirstOrDefault(ei => ei.eventName == beforeName);
                if (eventItem == null)
                    continue;
                eventItem.triggerList.Sort((a, b) => a.trigger.compare(b.trigger, eventArg));
                triggerList.AddRange(eventItem.triggerList.Select(ti => ti.trigger));
            }
            triggerList.Sort((a, b) => a.compare(b, eventArg));
            while (triggerList.Count > 0)
            {
                ITrigger trigger = triggerList[0];
                triggerList.RemoveAt(0);
                if (eventArg.isCanceled)
                    break;
                if (trigger is ITrigger<T> triggerT)
                    await triggerT.invoke(eventArg);
                else
                    await trigger.invoke(eventArg);
                if (_insertEventList.Count > 0)
                {
                    foreach (string beforeName in beforeNames)
                    {
                        EventListItem insertEventItem = _insertEventList.FirstOrDefault(ei => ei.eventName == beforeName);
                        if (insertEventItem == null)
                            continue;
                        if (insertEventItem.triggerList.Count > 0)
                        {
                            EventListItem eventItem = _eventList.FirstOrDefault(ei => ei.eventName == beforeName);
                            if (eventItem != null)
                                eventItem.triggerList.Sort((a, b) => a.trigger.compare(b.trigger, eventArg));
                            triggerList.AddRange(insertEventItem.triggerList.Select(ti => ti.trigger));
                            triggerList.Sort((a, b) => a.compare(b, eventArg));
                            _insertEventList.Remove(insertEventItem);
                        }
                    }
                }
            }
            //Event
            int repeatTime = 0;
            do
            {
                if (eventArg.isCanceled)
                    break;
                if (eventArg.action != null)
                    await eventArg.action.Invoke(eventArg);
                repeatTime++;
            }
            while (repeatTime <= eventArg.repeatTime);
            //After
            IEnumerable<string> afterNames = eventArg.afterNames;
            if (afterNames == null)
                afterNames = new string[] { getNameAfter<T>() };
            else
                afterNames = afterNames.Concat(new string[] { getNameAfter<T>() });
            afterNames = afterNames.Distinct().ToArray();
            triggerList.Clear();
            foreach (string afterName in afterNames)
            {
                EventListItem eventItem = _eventList.FirstOrDefault(ei => ei.eventName == afterName);
                if (eventItem == null)
                    continue;
                eventItem.triggerList.Sort((a, b) => a.trigger.compare(b.trigger, eventArg));
                triggerList.AddRange(eventItem.triggerList.Select(ti => ti.trigger));
            }
            triggerList.Sort((a, b) => a.compare(b, eventArg));
            while (triggerList.Count > 0)
            {
                ITrigger trigger = triggerList[0];
                triggerList.RemoveAt(0);
                if (eventArg.isCanceled)
                    break;
                if (trigger is ITrigger<T> triggerT)
                    await triggerT.invoke(eventArg);
                else
                    await trigger.invoke(eventArg);
                if (_insertEventList.Count > 0)
                {
                    foreach (string afterName in afterNames)
                    {
                        EventListItem insertEventItem = _insertEventList.FirstOrDefault(ei => ei.eventName == afterName);
                        if (insertEventItem == null)
                            continue;
                        if (insertEventItem.triggerList.Count > 0)
                        {
                            EventListItem eventItem = _eventList.FirstOrDefault(ei => ei.eventName == afterName);
                            if (eventItem != null)
                                eventItem.triggerList.Sort((a, b) => a.trigger.compare(b.trigger, eventArg));
                            triggerList.AddRange(insertEventItem.triggerList.Select(ti => ti.trigger));
                            triggerList.Sort((a, b) => a.compare(b, eventArg));
                            _insertEventList.Remove(insertEventItem);
                        }
                    }
                }
            }
            _eventChainList.Remove(eventArgItem);
        }
        [Serializable]
        public class EventArgItem
        {
            public IEventArg eventArg { get; set; }
        }
        [SerializeField]
        List<EventArgItem> _eventChainList = new List<EventArgItem>();
        public IEventArg currentEvent => _eventChainList.Count > 0 ? _eventChainList[_eventChainList.Count - 1].eventArg : null;
        public IEventArg[] getEventChain()
        {
            return _eventChainList.Select(ei => ei.eventArg).ToArray();
        }
    }
    public class Trigger : Trigger<IEventArg>
    {
        public Trigger(Func<object[], Task> action = null, Func<ITrigger, ITrigger, IEventArg, int> comparsion = null) : base(arg =>
        {
            if (action != null)
                return action.Invoke(arg.args);
            else
                return Task.CompletedTask;
        }, comparsion)
        {
        }
    }
    public class Trigger<T> : ITrigger<T> where T : IEventArg
    {
        public Func<ITrigger, ITrigger, IEventArg, int> comparsion { get; }
        public Func<T, bool> condition { get; }
        public Func<T, Task> action { get; }
        public Trigger(Func<T, Task> action = null, Func<ITrigger, ITrigger, IEventArg, int> comparsion = null)
        {
            this.action = action;
            this.comparsion = comparsion;
        }
        public int compare(ITrigger<T> other, T arg)
        {
            return compare(other, arg);
        }
        public int compare(ITrigger other, IEventArg arg)
        {
            if (comparsion != null)
                return comparsion.Invoke(this, other, arg);
            else
                return 0;
        }
        public bool checkCondition(T arg)
        {
            if (condition != null)
                return condition.Invoke(arg);
            else
                return true;
        }
        public bool checkCondition(IEventArg arg)
        {
            return checkCondition((T)arg);
        }
        public Task invoke(T arg)
        {
            if (action != null)
                return action.Invoke(arg);
            else
                return Task.CompletedTask;
        }
        public Task invoke(IEventArg arg)
        {
            if (arg is T t)
                return invoke(t);
            else
                return Task.CompletedTask;
        }
    }
    public class GeneratedEventArg : IEventArg
    {
        public string[] beforeNames { get; set; } = new string[0];
        public string[] afterNames { get; set; } = new string[0];
        public object[] args { get; set; }
        public bool isCanceled { get; set; } = false;
        public int repeatTime { get; set; } = 0;
        public Func<IEventArg, Task> action { get; set; }

        public GeneratedEventArg(string[] eventNames, object[] args)
        {
            this.afterNames = eventNames;
            this.args = args;
        }
    }
    public static class EventArgExtension
    {
        public static void replaceAction<T>(this T eventArg, Func<T, Task> action) where T : IEventArg
        {
            eventArg.action = arg =>
            {
                if (action != null)
                    return action.Invoke(eventArg);
                else
                    return Task.CompletedTask;
            };
        }
    }
    [Serializable]
    public class RepeatRegistrationException : Exception
    {
        public RepeatRegistrationException() { }
        public RepeatRegistrationException(string eventName, ITrigger trigger) : base(trigger + "重复注册事件" + eventName)
        { }
        public RepeatRegistrationException(string message) : base(message) { }
        public RepeatRegistrationException(string message, Exception inner) : base(message, inner) { }
        protected RepeatRegistrationException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
}