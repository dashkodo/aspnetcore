// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Linq;
using Xunit;

namespace Microsoft.AspNetCore.Http.Features
{
    public class QueryFeatureTests
    {
        [Fact]
        public void QueryReturnsParsedQueryCollection()
        {
            // Arrange
            var features = new FeatureCollection();
            features[typeof(IHttpRequestFeature)] = new HttpRequestFeature { QueryString = "foo=bar" };

            var provider = new QueryFeature(features);

            // Act
            var queryCollection = provider.Query;

            // Assert
            Assert.Equal("bar", queryCollection["foo"]);
        }

        [Theory]
        [InlineData("?key1=value1&key2=value2")]
        [InlineData("key1=value1&key2=value2")]
        public void ParseQueryWithUniqueKeysWorks(string queryString)
        {
            var features = new FeatureCollection();
            features[typeof(IHttpRequestFeature)] = new HttpRequestFeature { QueryString = queryString };

            var provider = new QueryFeature(features);

            var queryCollection = provider.Query;

            Assert.Equal(2, queryCollection.Count);
            Assert.Equal("value1", queryCollection["key1"].FirstOrDefault());
            Assert.Equal("value2", queryCollection["key2"].FirstOrDefault());
        }

        [Theory]
        [InlineData("?q", "q")]
        [InlineData("?q&", "q")]
        [InlineData("?q1=abc&q2", "q2")]
        [InlineData("?q=", "q")]
        [InlineData("?q=&", "q")]
        public void KeyWithoutValuesAddedToQueryCollection(string queryString, string emptyParam)
        {
            var features = new FeatureCollection();
            features[typeof(IHttpRequestFeature)] = new HttpRequestFeature { QueryString = queryString };

            var provider = new QueryFeature(features);

            var queryCollection = provider.Query;

            Assert.True(queryCollection.Keys.Contains(emptyParam));
            Assert.Equal(string.Empty, queryCollection[emptyParam]);
        }

        [Theory]
        [InlineData("?&&")]
        [InlineData("?&")]
        [InlineData("&&")]
        public void EmptyKeysNotAddedToQueryCollection(string queryString)
        {
            var features = new FeatureCollection();
            features[typeof(IHttpRequestFeature)] = new HttpRequestFeature { QueryString = queryString };

            var provider = new QueryFeature(features);

            var queryCollection = provider.Query;

            Assert.Equal(0, queryCollection.Count);
        }

        [Fact]
        public void ParseQueryWithEmptyKeyWorks()
        {
            var features = new FeatureCollection();
            features[typeof(IHttpRequestFeature)] = new HttpRequestFeature { QueryString = "?=value1&=" };

            var provider = new QueryFeature(features);

            var queryCollection = provider.Query;

            Assert.Single(queryCollection);
            Assert.Equal(new[] { "value1", "" }, queryCollection[""]);
        }

        [Fact]
        public void ParseQueryWithDuplicateKeysGroups()
        {
            var features = new FeatureCollection();
            features[typeof(IHttpRequestFeature)] = new HttpRequestFeature { QueryString = "?key1=valueA&key2=valueB&key1=valueC" };

            var provider = new QueryFeature(features);

            var queryCollection = provider.Query;

            Assert.Equal(2, queryCollection.Count);
            Assert.Equal(new[] { "valueA", "valueC" }, queryCollection["key1"]);
            Assert.Equal("valueB", queryCollection["key2"].FirstOrDefault());
        }

        [Fact]
        public void ParseQueryWithThreefoldKeysGroups()
        {
            var features = new FeatureCollection();
            features[typeof(IHttpRequestFeature)] = new HttpRequestFeature { QueryString = "?key1=valueA&key2=valueB&key1=valueC&key1=valueD" };

            var provider = new QueryFeature(features);

            var queryCollection = provider.Query;

            Assert.Equal(2, queryCollection.Count);
            Assert.Equal(new[] { "valueA", "valueC", "valueD" }, queryCollection["key1"]);
            Assert.Equal("valueB", queryCollection["key2"].FirstOrDefault());
        }

        [Fact]
        public void ParseQueryWithEmptyValuesWorks()
        {
            var features = new FeatureCollection();
            features[typeof(IHttpRequestFeature)] = new HttpRequestFeature { QueryString = "?key1=&key2=" };

            var provider = new QueryFeature(features);

            var queryCollection = provider.Query;

            Assert.Equal(2, queryCollection.Count);
            Assert.Equal(string.Empty, queryCollection["key1"].FirstOrDefault());
            Assert.Equal(string.Empty, queryCollection["key2"].FirstOrDefault());
        }

        [Theory]
        [InlineData("?")]
        [InlineData("")]
        [InlineData(null)]
        public void ParseEmptyOrNullQueryWorks(string queryString)
        {
            var features = new FeatureCollection();
            features[typeof(IHttpRequestFeature)] = new HttpRequestFeature { QueryString = queryString };

            var provider = new QueryFeature(features);

            var queryCollection = provider.Query;

            Assert.Empty(queryCollection);
        }
    }
}
