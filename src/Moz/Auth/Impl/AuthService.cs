﻿using System;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moz.Auth.Attributes;
using Moz.Bus.Dtos;
using Moz.Bus.Dtos.Auth;
using Moz.Bus.Dtos.Members;
using Moz.Bus.Dtos.Request.Members;
using Moz.Bus.Models.Common;
using Moz.Bus.Models.Members;
using Moz.Bus.Services;
using Moz.Bus.Services.Members;
using Moz.Core.Config;
using Moz.DataBase;
using Moz.Exceptions;
using Moz.Service.Security;

namespace Moz.Auth.Impl
{
    internal class AuthService : BaseService,IAuthService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IMemberService _memberService;
        private readonly IEncryptionService _encryptionService;
        private readonly IOptions<AppConfig> _appConfig;
        private readonly IDistributedCache _distributedCache;
        private readonly IJwtService _jwtService;
        private readonly IRegistrationService _registrationService;

        public AuthService(IHttpContextAccessor httpContextAccessor,
            IMemberService memberService,
            IEncryptionService encryptionService,
            IOptions<AppConfig> appConfig,
            IDistributedCache distributedCache, 
            IJwtService jwtService,
            IRegistrationService registrationService)
        { 
            _httpContextAccessor = httpContextAccessor;
            _memberService = memberService;
            _encryptionService = encryptionService;
            _appConfig = appConfig;
            _distributedCache = distributedCache;
            _jwtService = jwtService;
            _registrationService = registrationService;
        }
        
        #region Utils
        private bool PasswordsMatch(string passwordInDb, string salt, string enteredPassword)
        {
            if (string.IsNullOrEmpty(passwordInDb) ||
                string.IsNullOrEmpty(salt) ||
                string.IsNullOrEmpty(enteredPassword))
                return false;
            var savedPassword = _encryptionService.CreatePasswordHash(enteredPassword, salt,"SHA512");
            return passwordInDb.Equals(savedPassword, StringComparison.OrdinalIgnoreCase);
        }
        #endregion

        /// <summary>
        /// 获取uid
        /// </summary>
        /// <returns></returns>
        public ServResult<string> GetAuthenticatedUId()
        {
            var result = _httpContextAccessor 
                .HttpContext
                .AuthenticateAsync(MozAuthAttribute.MozAuthorizeSchemes)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            if (!result?.Principal?.Identity?.IsAuthenticated??false) 
                return Error("未登录",401);
            
            var claim = result?.Principal?.Claims?.FirstOrDefault(o => o.Type == "jti");
            if (claim == null || claim.Value.IsNullOrEmpty()) 
                return Error("未登录",401);

            return claim.Value;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public ServResult<SimpleMember> GetAuthenticatedMember()
        {
            var result = GetAuthenticatedUId();
            if (result.Code>0)
                return Error(result.Message,result.Code);
            
            if(string.IsNullOrEmpty(result.Data))
                return Error("未登陆",401);
            
            var member = _memberService.GetSimpleMemberByUId(result.Data);
            
            if (member == null)
                return Error("找不到用户",404);
            if (!member.IsActive)
                return Error("用户未激活");
            if (member.IsDelete) 
                return Error("用户已删除");
            if (member.CannotLoginUntilDate.HasValue && member.CannotLoginUntilDate.Value > DateTime.UtcNow)
                return Error("用户已被关小黑屋");

            return member;
        }
        
        
        /// <summary>
        /// 用户名密码登录
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public ServResult<MemberLoginApo> LoginWithUsernamePassword(ServRequest<LoginWithUsernamePasswordDto> request)
        {
            static LoginWithUsernamePasswordQueryableItem GetMember(string username)
            {
                // ReSharper disable once ConvertToUsingDeclaration
                using (var client = DbFactory.GetClient())
                {
                    return client.Queryable<Member>()
                        .Select(it => new LoginWithUsernamePasswordQueryableItem
                        {
                            Id = it.Id,
                            UId = it.UId,
                            Username = it.Username,
                            Password = it.Password,
                            PasswordSalt = it.PasswordSalt,
                            IsDelete = it.IsDelete,
                            IsActive = it.IsActive,
                            CannotLoginUntilDate = it.CannotLoginUntilDate
                        })
                        .Single(t => t.Username.Equals(username));
                }
            }

            var member = GetMember(request.Data.Username);
            if (member == null)
            {
                return Error("用户不存在");
            }

            if (member.UId.IsNullOrEmpty())
            {
                return Error("用户UID为空");
            }

            if (member.IsDelete)
            {
                return Error("用户已删除");
            }

            if (!member.IsActive)
            {
                return Error("用户未激活");
            }

            if (member.CannotLoginUntilDate != null && member.CannotLoginUntilDate.Value > DateTime.UtcNow)
            {
                return Error("用户未解封，请等待");
            }

            if (!PasswordsMatch(member.Password, member.PasswordSalt, request.Data.Password))
            {
                return Error("密码错误");
            }

            var tokenInfo = _jwtService.GenerateTokenInfo(member.UId);
            return new MemberLoginApo
            {
                AccessToken = tokenInfo.JwtToken,
                RefreshToken = tokenInfo.RefreshToken,
                ExpireDateTime = tokenInfo.ExpireDateTime
            };
        }

        /// <summary>
        /// 三方平台登录
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public ServResult<MemberLoginApo> ExternalAuth(ServRequest<ExternalAuthInfo> request)
        {
            ExternalAuthentication externalAuthentication;
            string memberUId;
            using (var db = DbFactory.GetClient())
            {
                externalAuthentication = db.Queryable<ExternalAuthentication>()
                    .Single(t =>
                        t.Openid.Equals(request.Data.OpenId, StringComparison.OrdinalIgnoreCase) &&
                        t.Provider == request.Data.Provider);
            }

            if (externalAuthentication == null)
            {
                var registerResult = _registrationService.Register(new ExternalRegistrationRequest
                {
                    Provider = request.Data.Provider,
                    AccessToken = request.Data.AccessToken,
                    ExpireDt = request.Data.ExpireDt,
                    OpenId = request.Data.OpenId,
                    RefreshToken = request.Data.RefreshToken,
                    UserInfo = request.Data.UserInfo
                });
                if (registerResult.Code > 0)
                {
                    return Error(registerResult.Message, registerResult.Code);
                }
                memberUId = registerResult.Data.MemberUId;
            }
            else
            {
                var memberId = externalAuthentication.MemberId;
                using (var db = DbFactory.GetClient())
                {
                    var member = db.Queryable<Member>()
                        .Select(it => new {it.Id, it.UId, it.Avatar,it.IsDelete, it.IsActive, it.CannotLoginUntilDate})
                        .Single(it => it.Id == memberId);
                    
                    if (member == null)
                    {
                        return Error("用户不存在");
                    }

                    if (member.UId.IsNullOrEmpty())
                    {
                        return Error("用户UID为空");
                    }

                    if (member.IsDelete)
                    {
                        return Error("用户已删除");
                    }

                    if (!member.IsActive)
                    {
                        return Error("用户未激活");
                    }

                    if (member.CannotLoginUntilDate != null && member.CannotLoginUntilDate.Value > DateTime.UtcNow)
                    {
                        return Error("用户未解封，请等待");
                    }

                    externalAuthentication.AccessToken = request.Data.AccessToken;
                    externalAuthentication.ExpireDt = request.Data.ExpireDt;
                    externalAuthentication.RefreshToken = request.Data.RefreshToken;

                    db.UseTran(tran =>
                    {
                        tran.Updateable(externalAuthentication).ExecuteCommand();

                        if (member.Avatar.IsNullOrEmpty() &&
                            !(request?.Data?.UserInfo?.Avatar?.IsNullOrEmpty() ?? true))
                        {
                            tran.Updateable<Member>()
                                .SetColumns(it => new Member()
                                {
                                    Avatar = request.Data.UserInfo.Avatar
                                })
                                .Where(it => it.Id == memberId)
                                .ExecuteCommand();
                        }
                    });

                    memberUId = member.UId;
                }
            }

            var tokenInfo = _jwtService.GenerateTokenInfo(memberUId);
            return new MemberLoginApo
            {
                AccessToken = tokenInfo.JwtToken,
                RefreshToken = tokenInfo.RefreshToken,
                ExpireDateTime = tokenInfo.ExpireDateTime
            };
        }

        /// <summary>
        /// 设置cookie，用于web登录
        /// </summary>
        /// <param name="request"></param>
        /// <exception cref="MozException"></exception>
        public ServResult SetAuthCookie(ServRequest<SetAuthCookieDto> request)
        {
            if(request.Data.Token.IsNullOrEmpty())
                return Error("Token不能为空");
            
            var key = _appConfig.Value.AppSecret ?? "gvPXwK50tpE9b6P7";
            var encryptToken = _encryptionService.EncryptText(request.Data.Token, key);
            
            _httpContextAccessor.HttpContext?.Response?.Cookies?.Append("__moz__token",encryptToken,new CookieOptions()
            {
                Path = "/",
                HttpOnly = true
            });
            return Ok();
        }
        
        /// <summary>
        /// 退出web登录
        /// </summary>
        public ServResult RemoveAuthCookie()
        {
            _httpContextAccessor.HttpContext?.Response?.Cookies?.Delete("__moz__token");
            return Ok();
        }
        
    }
    
    internal class LoginWithUsernamePasswordQueryableItem
    {
        public long Id { get; set; }
        public string UId { get; set; }
        public string Username { get; set; }
        public string Password{ get; set; }
        public string PasswordSalt { get; set; }
        public bool IsDelete{ get; set; }
        public bool IsActive { get; set; }
        public DateTime? CannotLoginUntilDate{ get; set; }
    }
}