﻿using System;
using System.Security.Cryptography.X509Certificates;
using Digst.OioIdws.Test.Common;
using Digst.OioIdws.Wsc.OioWsTrust;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Digst.OioIdws.Test
{
    [TestClass]
    public class StsCommunicationTests
    {
        /// <summary>
        /// This integration test verifies that the implementation is working according to the [NEMLOGIN-STSRULES] specification and that life time of token is as expected.
        /// It is assumed that if the STS in NemLog-in test integration environment response succesfully ... then the implementation is according to the [NEMLOGIN-STSRULES] specification.
        /// Prerequisites:
        /// MOCES client certificate https://test-nemlog-in.dk/Testportal/certifikater/%C3%98S%20-%20Morten%20Mortensen%20RID%2093947552.html must be installed in user store (it automatically is)
        /// FOCES STS certificate https://test-nemlog-in.dk/Testportal/certifikater/IntegrationTestSigning.zip must be installed in local store.
        /// </summary>
        [TestMethod]
        [TestCategory(Constants.IntegrationTest)]
        public void GetToken1HourLifeTimeTest()
        {
            // Arrange
            ITokenService tokenService = new TokenService();
            var oioIdwsWscConfiguration = new Configuration();
            var clientCertificate = new Certificate
            {
                StoreLocation = StoreLocation.CurrentUser,
                StoreName = StoreName.My,
                X509FindType = X509FindType.FindByThumbprint,
                FindValue = "41 49 9f b4 53 f7 2b ec e4 e8 92 b3 c1 5d 32 0d ef 0f ad aa"
            };
            oioIdwsWscConfiguration.ClientCertificate = clientCertificate;
            oioIdwsWscConfiguration.StsCertificate = new Certificate
            {
                StoreLocation = StoreLocation.LocalMachine,
                StoreName = StoreName.My,
                X509FindType = X509FindType.FindByThumbprint,
                FindValue = "2e7a061560fa2c5e141a634dc1767dacaeec8d12"
            };
            oioIdwsWscConfiguration.StsEndpointAddress =
                "https://SecureTokenService.test-nemlog-in.dk/SecurityTokenService.svc";
            oioIdwsWscConfiguration.WspEndpointID = "https://saml.nnit001.dmz.inttest";
            oioIdwsWscConfiguration.TokenLifeTimeInMinutes = 60;

            // Act
            var securityToken = tokenService.GetToken(oioIdwsWscConfiguration);

            // Assert
            Assert.IsNotNull(securityToken);
            // 30 seconds withdrawn in order to allow some time sync issues.
            Assert.IsTrue(securityToken.ValidTo > DateTime.UtcNow.AddHours(1).AddSeconds(-30), "Life time of token was not one hour!");
        }
    }
}