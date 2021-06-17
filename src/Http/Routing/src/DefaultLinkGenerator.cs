// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Routing
{
    internal sealed partial class DefaultLinkGenerator : LinkGenerator, IDisposable
    {
        private readonly ParameterPolicyFactory _parameterPolicyFactory;
        private readonly TemplateBinderFactory _binderFactory;
        private readonly ILogger<DefaultLinkGenerator> _logger;
        private readonly IServiceProvider _serviceProvider;

        // A LinkOptions object initialized with the values from RouteOptions
        // Used when the user didn't specify something more global.
        private readonly LinkOptions _globalLinkOptions;

        // Caches TemplateBinder instances
        private readonly DataSourceDependentCache<ConcurrentDictionary<RouteEndpoint, TemplateBinder>> _cache;

        // Used to initialize TemplateBinder instances
        private readonly Func<RouteEndpoint, TemplateBinder> _createTemplateBinder;

        public DefaultLinkGenerator(
            ParameterPolicyFactory parameterPolicyFactory,
            TemplateBinderFactory binderFactory,
            EndpointDataSource dataSource,
            IOptions<RouteOptions> routeOptions,
            ILogger<DefaultLinkGenerator> logger,
            IServiceProvider serviceProvider)
        {
            _parameterPolicyFactory = parameterPolicyFactory;
            _binderFactory = binderFactory;
            _logger = logger;
            _serviceProvider = serviceProvider;

            // We cache TemplateBinder instances per-Endpoint for performance, but we want to wipe out
            // that cache is the endpoints change so that we don't allow unbounded memory growth.
            _cache = new DataSourceDependentCache<ConcurrentDictionary<RouteEndpoint, TemplateBinder>>(dataSource, (_) =>
            {
                // We don't eagerly fill this cache because there's no real reason to. Unlike URL matching, we don't
                // need to build a big data structure up front to be correct.
                return new ConcurrentDictionary<RouteEndpoint, TemplateBinder>();
            });

            // Cached to avoid per-call allocation of a delegate on lookup.
            _createTemplateBinder = CreateTemplateBinder;

            _globalLinkOptions = new LinkOptions()
            {
                AppendTrailingSlash = routeOptions.Value.AppendTrailingSlash,
                LowercaseQueryStrings = routeOptions.Value.LowercaseQueryStrings,
                LowercaseUrls = routeOptions.Value.LowercaseUrls,
            };
        }

        public override string? GetPathByAddress<TAddress>(
            HttpContext httpContext,
            TAddress address,
            RouteValueDictionary values,
            RouteValueDictionary? ambientValues = default,
            PathString? pathBase = default,
            FragmentString fragment = default,
            LinkOptions? options = null)
        {
            if (httpContext == null)
            {
                throw new ArgumentNullException(nameof(httpContext));
            }

            var endpoints = GetEndpoints(address);
            if (endpoints.Count == 0)
            {
                return null;
            }

            return GetPathByEndpoints(
                httpContext,
                endpoints,
                values,
                ambientValues,
                pathBase ?? httpContext.Request.PathBase,
                fragment,
                options);
        }

        public override string? GetPathByAddress<TAddress>(
            TAddress address,
            RouteValueDictionary values,
            PathString pathBase = default,
            FragmentString fragment = default,
            LinkOptions? options = null)
        {
            var endpoints = GetEndpoints(address);
            if (endpoints.Count == 0)
            {
                return null;
            }

            return GetPathByEndpoints(
                httpContext: null,
                endpoints,
                values,
                ambientValues: null,
                pathBase: pathBase,
                fragment: fragment,
                options: options);
        }

        public override string? GetUriByAddress<TAddress>(
            HttpContext httpContext,
            TAddress address,
            RouteValueDictionary values,
            RouteValueDictionary? ambientValues = default,
            string? scheme = default,
            HostString? host = default,
            PathString? pathBase = default,
            FragmentString fragment = default,
            LinkOptions? options = null)
        {
            if (httpContext == null)
            {
                throw new ArgumentNullException(nameof(httpContext));
            }

            var endpoints = GetEndpoints(address);
            if (endpoints.Count == 0)
            {
                return null;
            }

            return GetUriByEndpoints(
                endpoints,
                values,
                ambientValues,
                scheme ?? httpContext.Request.Scheme,
                host ?? httpContext.Request.Host,
                pathBase ?? httpContext.Request.PathBase,
                fragment,
                options);
        }

        public override string? GetUriByAddress<TAddress>(
            TAddress address,
            RouteValueDictionary values,
            string? scheme,
            HostString host,
            PathString pathBase = default,
            FragmentString fragment = default,
            LinkOptions? options = null)
        {
            if (string.IsNullOrEmpty(scheme))
            {
                throw new ArgumentException("A scheme must be provided.", nameof(scheme));
            }

            if (!host.HasValue)
            {
                throw new ArgumentException("A host must be provided.", nameof(host));
            }

            var endpoints = GetEndpoints(address);
            if (endpoints.Count == 0)
            {
                return null;
            }

            return GetUriByEndpoints(
                endpoints,
                values,
                ambientValues: null,
                scheme: scheme,
                host: host,
                pathBase: pathBase,
                fragment: fragment,
                options: options);
        }

        private List<RouteEndpoint> GetEndpoints<TAddress>(TAddress address)
        {
            var addressingScheme = _serviceProvider.GetRequiredService<IEndpointAddressScheme<TAddress>>();
            var endpoints = addressingScheme.FindEndpoints(address).OfType<RouteEndpoint>().ToList();

            if (endpoints.Count == 0)
            {
                Log.EndpointsNotFound(_logger, address);
            }
            else
            {
                Log.EndpointsFound(_logger, address, endpoints);
            }

            return endpoints;
        }

        private string? GetPathByEndpoints(
            HttpContext? httpContext,
            List<RouteEndpoint> endpoints,
            RouteValueDictionary values,
            RouteValueDictionary? ambientValues,
            PathString pathBase,
            FragmentString fragment,
            LinkOptions? options)
        {
            for (var i = 0; i < endpoints.Count; i++)
            {
                var endpoint = endpoints[i];
                if (TryProcessTemplate(
                    httpContext: httpContext,
                    endpoint: endpoint,
                    values: values,
                    ambientValues: ambientValues,
                    options: options,
                    result: out var result))
                {
                    var uri = UriHelper.BuildRelative(
                        pathBase,
                        result.path,
                        result.query,
                        fragment);
                    Log.LinkGenerationSucceeded(_logger, endpoints, uri);
                    return uri;
                }
            }

            Log.LinkGenerationFailed(_logger, endpoints);
            return null;
        }

        // Also called from DefaultLinkGenerationTemplate
        public string? GetUriByEndpoints(
            List<RouteEndpoint> endpoints,
            RouteValueDictionary values,
            RouteValueDictionary? ambientValues,
            string scheme,
            HostString host,
            PathString pathBase,
            FragmentString fragment,
            LinkOptions? options)
        {
            for (var i = 0; i < endpoints.Count; i++)
            {
                var endpoint = endpoints[i];
                if (TryProcessTemplate(
                    httpContext: null,
                    endpoint: endpoint,
                    values: values,
                    ambientValues: ambientValues,
                    options: options,
                    result: out var result))
                {
                    var uri = UriHelper.BuildAbsolute(
                        scheme,
                        host,
                        pathBase,
                        result.path,
                        result.query,
                        fragment);
                    Log.LinkGenerationSucceeded(_logger, endpoints, uri);
                    return uri;
                }
            }

            Log.LinkGenerationFailed(_logger, endpoints);
            return null;
        }

        private TemplateBinder CreateTemplateBinder(RouteEndpoint endpoint)
        {
            return _binderFactory.Create(endpoint.RoutePattern);
        }

        // Internal for testing
        internal TemplateBinder GetTemplateBinder(RouteEndpoint endpoint) => _cache.EnsureInitialized().GetOrAdd(endpoint, _createTemplateBinder);

        // Internal for testing
        internal bool TryProcessTemplate(
            HttpContext? httpContext,
            RouteEndpoint endpoint,
            RouteValueDictionary values,
            RouteValueDictionary? ambientValues,
            LinkOptions? options,
            out (PathString path, QueryString query) result)
        {
            var templateBinder = GetTemplateBinder(endpoint);

            var templateValuesResult = templateBinder.GetValues(ambientValues, values);
            if (templateValuesResult == null)
            {
                // We're missing one of the required values for this route.
                result = default;
                Log.TemplateFailedRequiredValues(_logger, endpoint, ambientValues, values);
                return false;
            }

            if (!templateBinder.TryProcessConstraints(httpContext, templateValuesResult.CombinedValues, out var parameterName, out var constraint))
            {
                result = default;
                Log.TemplateFailedConstraint(_logger, endpoint, parameterName, constraint, templateValuesResult.CombinedValues);
                return false;
            }

            if (!templateBinder.TryBindValues(templateValuesResult.AcceptedValues, options, _globalLinkOptions, out result))
            {
                Log.TemplateFailedExpansion(_logger, endpoint, templateValuesResult.AcceptedValues);
                return false;
            }

            Log.TemplateSucceeded(_logger, endpoint, result.path, result.query);
            return true;
        }

        // Also called from DefaultLinkGenerationTemplate
        public static RouteValueDictionary? GetAmbientValues(HttpContext? httpContext)
        {
            return httpContext?.Features.Get<IRouteValuesFeature>()?.RouteValues;
        }

        public void Dispose()
        {
            _cache.Dispose();
        }

#nullable disable
        private static partial class Log
        {
            [LoggerMessage(EventId = 100, EventName = "EndpointsFound", Level = LogLevel.Debug, Message = "Found the endpoints {Endpoints} for address {Address}")]
            public static partial void EndpointsFound(ILogger logger, object address, IEnumerable<Endpoint> endpoints);

            [LoggerMessage(EventId = 101, EventName = "EndpointsNotFound", Level = LogLevel.Debug, Message = "No endpoints found for address {Address}")]
            public static partial void EndpointsNotFound(ILogger logger, object address);

            public static void TemplateSucceeded(ILogger logger, RouteEndpoint endpoint, PathString path, QueryString query)
                => TemplateSucceeded(logger, endpoint.RoutePattern.RawText, endpoint.DisplayName, path.Value, query.Value);

            [LoggerMessage(EventId = 102, EventName = "TemplateSucceeded", Level = LogLevel.Debug,
                Message = "Successfully processed template {Template} for {Endpoint} resulting in {Path} and {Query}")]
            public static partial void TemplateSucceeded(ILogger logger, string template, string endpoint, string path, string query);

            public static void TemplateFailedRequiredValues(ILogger logger, RouteEndpoint endpoint, RouteValueDictionary ambientValues, RouteValueDictionary values)
            {
                // Checking level again to avoid allocation on the common path
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    TemplateFailedRequiredValues(logger, endpoint.RoutePattern.RawText, endpoint.DisplayName, FormatRouteValues(ambientValues), FormatRouteValues(values), FormatRouteValues(endpoint.RoutePattern.Defaults));
                }
            }

            [LoggerMessage(EventId = 103, EventName = "TemplateFailedRequiredValues", Level = LogLevel.Debug,
                Message = "Failed to process the template {Template} for {Endpoint}. " +
                "A required route value is missing, or has a different value from the required default values. " +
                "Supplied ambient values {AmbientValues} and {Values} with default values {Defaults}")]
            public static partial void TemplateFailedRequiredValues(ILogger logger, string template, string endpoint, string ambientValues, string values, string defaults);

            public static void TemplateFailedConstraint(ILogger logger, RouteEndpoint endpoint, string parameterName, IRouteConstraint constraint, RouteValueDictionary values)
            {
                // Checking level again to avoid allocation on the common path
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    TemplateFailedConstraint(logger, endpoint.RoutePattern.RawText, endpoint.DisplayName, parameterName, constraint, FormatRouteValues(values));
                }
            }

            [LoggerMessage(EventId = 107, EventName = "TemplateFailedConstraint", Level = LogLevel.Debug,
                Message = "Failed to process the template {Template} for {Endpoint}. " +
                "The constraint {Constraint} for parameter {ParameterName} failed with values {Values}")]
            public static partial void TemplateFailedConstraint(ILogger logger, string template, string endpoint, string parameterName, IRouteConstraint constraint, string values);

            public static void TemplateFailedExpansion(ILogger logger, RouteEndpoint endpoint, RouteValueDictionary values)
            {
                // Checking level again to avoid allocation on the common path
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    TemplateFailedExpansion(logger, endpoint.RoutePattern.RawText, endpoint.DisplayName, FormatRouteValues(values));
                }
            }

            [LoggerMessage(EventId = 104, EventName = "TemplateFailedExpansion", Level = LogLevel.Debug,
                Message = "Failed to process the template {Template} for {Endpoint}. " +
                "The failure occurred while expanding the template with values {Values} " +
                "This is usually due to a missing or empty value in a complex segment")]
            public static partial void TemplateFailedExpansion(ILogger logger, string template, string endpoint, string values);

            public static void LinkGenerationSucceeded(ILogger logger, IEnumerable<Endpoint> endpoints, string uri)
            {
                // Checking level again to avoid allocation on the common path
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    LinkGenerationSucceeded(logger, endpoints.Select(e => e.DisplayName), uri);
                }
            }

            [LoggerMessage(EventId = 105, EventName = "LinkGenerationSucceeded", Level = LogLevel.Debug,
                Message = "Link generation succeeded for endpoints {Endpoints} with result {URI}")]
            public static partial void LinkGenerationSucceeded(ILogger logger, IEnumerable<string> endpoints, string uri);

            public static void LinkGenerationFailed(ILogger logger, IEnumerable<Endpoint> endpoints)
            {
                // Checking level again to avoid allocation on the common path
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    LinkGenerationFailed(logger, endpoints.Select(e => e.DisplayName));
                }
            }

            [LoggerMessage(EventId = 106, EventName = "LinkGenerationFailed", Level = LogLevel.Debug,
                Message = "Link generation failed for endpoints {Endpoints}")]
            public static partial void LinkGenerationFailed(ILogger logger, IEnumerable<string> endpoints);

            // EXPENSIVE: should only be used at Debug and higher levels of logging.
            private static string FormatRouteValues(IReadOnlyDictionary<string, object> values)
            {
                if (values == null || values.Count == 0)
                {
                    return "{ }";
                }

                var builder = new StringBuilder();
                builder.Append("{ ");

                foreach (var kvp in values.OrderBy(kvp => kvp.Key))
                {
                    builder.Append('"');
                    builder.Append(kvp.Key);
                    builder.Append('"');
                    builder.Append(':');
                    builder.Append(' ');
                    builder.Append('"');
                    builder.Append(kvp.Value);
                    builder.Append('"');
                    builder.Append(", ");
                }

                // Trim trailing ", "
                builder.Remove(builder.Length - 2, 2);

                builder.Append(" }");

                return builder.ToString();
            }
        }
    }
}
