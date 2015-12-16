﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IdentityModel.Tokens;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Xml;
using Digst.OioIdws.OioWsTrust;
using Digst.OioIdws.Rest.AuthorizationService.Issuing;
using Digst.OioIdws.Rest.AuthorizationService.Storage;
using Digst.OioIdws.Rest.Common;
using Digst.OioIdws.Test.Common;
using Microsoft.Owin;
using Microsoft.Owin.Logging;
using Microsoft.Owin.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Newtonsoft.Json.Linq;
using Owin;

namespace Digst.OioIdws.Rest.AuthorizationService.Tests
{
    [TestClass]
    public class AuthorizationEndpointTests
    {
        [TestMethod]
        [TestCategory(Constants.UnitTest)]
        public async Task IssueAccessToken_Success_ReturnsCorrectly()
        {
            var requestSamlToken = Utils.ToBase64("mit token");
            var accessTokenValue = "accesstoken1";

            var accessTokenGeneratorMock = new Mock<IAccessTokenGenerator>();
            var tokenStoreMock = new Mock<ISecurityTokenStore>();
            var tokenValidatorMock = new Mock<ITokenValidator>();

            accessTokenGeneratorMock
                .Setup(x => x.GenerateAccesstoken())
                .Returns(accessTokenValue);

            tokenValidatorMock
                .Setup(x => x.ValidateToken(requestSamlToken, It.IsAny<X509Certificate2>(), It.IsAny<OioIdwsAuthorizationServiceMiddleware.Settings>()))
                .Returns(new TokenValidationResult
                {
                    Success = true,
                    ClaimsIdentity = new ClaimsIdentity()
                });

            var options = new OioIdwsAuthorizationServiceOptions
            {
                IssueAccessTokenEndpoint = new PathString("/authorize"),
                AccessTokenGenerator = accessTokenGeneratorMock.Object,
                TokenValidator = tokenValidatorMock.Object,
            };
            using (var server = TestServer.Create(app =>
            {
                app.SetLoggerFactory(new OwinConsoleLoggerFactory());

                app.Use<OioIdwsAuthorizationServiceMiddleware>(app, options
                    ,tokenStoreMock.Object);
            }))
            { 
                var response = await server.HttpClient.PostAsync("/authorize",
                            new FormUrlEncodedContent(new[]
                            {new KeyValuePair<string, string>("saml-token", requestSamlToken),}));

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsNotNull(response.Content.Headers.ContentType);
                Assert.AreEqual("UTF-8", response.Content.Headers.ContentType.CharSet);
                Assert.AreEqual("application/json", response.Content.Headers.ContentType.MediaType);

                var accessToken = JObject.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(accessTokenValue, accessToken["access_token"]);
                Assert.AreEqual("bearer", accessToken["token_type"]);
                Assert.AreEqual(options.AccessTokenExpiration.TotalSeconds.ToString(CultureInfo.InvariantCulture), accessToken["expires_in"]);
            }

            accessTokenGeneratorMock.Verify(x => x.GenerateAccesstoken(), Times.Once);
        }

        [TestMethod]
        [TestCategory(Constants.UnitTest)]
        public async Task IssueAccessToken_OtherEndpoint_PassesThrough()
        {
            using (var server = TestServer.Create(app =>
            {
                app
                    .UseOioIdwsAuthorizationService(new OioIdwsAuthorizationServiceOptions
                    {
                        IssueAccessTokenEndpoint = new PathString("/authorize")
                    })
                    .Use((context, next) =>
                    {
                        context.Response.Write("finalmiddleware");
                        return Task.FromResult(0);
                    });
            }))
            {
                var response = await server.CreateRequest("/otherendpoint").PostAsync();
                var text = await response.Content.ReadAsStringAsync();
                Assert.IsTrue(text == "finalmiddleware");
            }
        }

        [TestMethod]
        [TestCategory(Constants.UnitTest)]
        public async Task IssueAccessToken_InvalidRequest_ReturnsUnauthorized()
        {
            var accessTokenGeneratorMock = new Mock<IAccessTokenGenerator>();
            var tokenStoreMock = new Mock<ISecurityTokenStore>();

            using (var server = TestServer.Create(app =>
            {
                app.Use<OioIdwsAuthorizationServiceMiddleware>(app, new OioIdwsAuthorizationServiceOptions
                {
                    IssueAccessTokenEndpoint = new PathString("/authorize"),
                    AccessTokenGenerator = accessTokenGeneratorMock.Object
                }, tokenStoreMock.Object);
            }))
            {
                var response = await server.CreateRequest("/authorize").PostAsync();
                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);

                var authHeader = response.Headers.WwwAuthenticate.Single(x => x.Scheme == "Bearer");
                var bearerParameters = HttpHeaderUtils.ParseBearerSchemeParameter(authHeader.Parameter);
                Assert.AreEqual(AuthenticationErrorCodes.InvalidRequest, bearerParameters["error"]);
                Assert.AreEqual(AuthenticationErrorCodes.Descriptions.SamlTokenMissing, bearerParameters["error_description"]);
            }
        }

        [TestMethod]
        [TestCategory(Constants.IntegrationTest)]
        public async Task IssueAccessTokenFromStsToken_ValidateSuccess_ReturnsCorrectly()
        {
            var requestSamlToken = GetSamlTokenXml();

            var tokenStore = new MemorySecurityTokenStore();

            var options = new OioIdwsAuthorizationServiceOptions
            {
                IssueAccessTokenEndpoint = new PathString("/authorize"),
            };
            using (var server = TestServer.Create(app =>
            {
                app.Use<OioIdwsAuthorizationServiceMiddleware>(app, options, tokenStore);
            }))
            {
                var response = await server.HttpClient.PostAsync("/authorize",
                            new FormUrlEncodedContent(new[]
                            {new KeyValuePair<string, string>("saml-token", requestSamlToken)}));

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var accessTokenJson = JObject.Parse(await response.Content.ReadAsStringAsync());
                var accessToken = (string) accessTokenJson["access_token"];

                var token = await tokenStore.RetrieveTokenAsync(accessToken);

                Assert.IsNotNull(token);
                Assert.AreEqual("34051178", token.Claims.Single(x => x.Type == "dk:gov:saml:attribute:CvrNumberIdentifier").Value);
            }
        }

        private string GetSamlTokenXml()
        {
            var tokenService = new TokenIssuingService();
            var securityToken = (GenericXmlSecurityToken) tokenService.RequestToken(new TokenIssuingRequestConfiguration
            {
                ClientCertificate = CertificateUtil.GetCertificate("0919ed32cf8758a002b39c10352be7dcccf1222a"),
                StsCertificate = CertificateUtil.GetCertificate("2e7a061560fa2c5e141a634dc1767dacaeec8d12"),
                SendTimeout = TimeSpan.FromDays(1),
                StsEndpointAddress = "https://SecureTokenService.test-nemlog-in.dk/SecurityTokenService.svc",
                TokenLifeTimeInMinutes = 5,
                WspEndpointId = "https://wsp.itcrew.dk"
            });

            return securityToken.TokenXml.OuterXml;
        }
    }
}