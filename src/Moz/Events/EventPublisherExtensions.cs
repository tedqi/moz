﻿using Moz.Bus.Models;
using Moz.CMS.Models;
using Moz.CMS.Model;
using Moz.CMS.Models;
using Moz.Domain.Models;

namespace Moz.Events
{
    public static class EventPublisherExtensions
    {
        public static void EntityCreated<T>(this IEventPublisher eventPublisher, T entity)
            where T : BaseModel
        {
            eventPublisher.Publish(new EntityCreated<T>(entity));
        }

        public static void EntityUpdated<T>(this IEventPublisher eventPublisher, T entity)
            where T : BaseModel
        {
            eventPublisher.Publish(new EntityUpdated<T>(entity));
        }

        public static void EntityDeleted<T>(this IEventPublisher eventPublisher, T entity)
            where T : BaseModel
        {
            eventPublisher.Publish(new EntityDeleted<T>(entity));
        } 
        
        public static void EntitiesDeleted<T>(this IEventPublisher eventPublisher, long[] ids)
            where T : BaseModel
        {
            eventPublisher.Publish(new EntitiesDeleted<T>(ids)); 
        }
    }
}