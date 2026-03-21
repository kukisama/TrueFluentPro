# 部署 APIM AI API

一键将 Azure OpenAI API（39 spec ops + 15 custom ops = **54 operations**）部署到现有的 APIM 服务实例中。  
提供**两种部署风格**，按需选择。

---

## 风格 A：静态声明式（推荐）

使用**预处理好的 spec**，纯 ARM 资源声明，零运行时依赖。

[![Deploy to Azure (Static)](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fkukisama%2FTrueFluentPro%2Fmain%2FARMs%2Fazuredeploy-static.json)

### 工作流程

```
点击按钮 → Azure Portal 填参数 → ARM 直接声明所有资源
     │
     ├── Backend         (MI 认证 → AI Services)
     ├── API             (导入预处理好的 spec, 39 ops)
     ├── Policy          (set-backend-service)
     ├── 15 Operations   (自定义操作: Chat/Completions/Audio/Images/Video/Responses)
     ├── 7 Tags          (服务级标签)
     └── 15 Tag关联       (operation ↔ tag)
         ═══════════════
         总计 54 operations
```

### 参数

| 参数 | 必填 | 默认值 | 说明 |
|------|------|--------|------|
| `apiName` | **是** | — | API 标识名（如 `ai03`） |
| `serviceName` | 否 | `APIMzpl` | APIM 服务实例名称 |
| `displayName` | 否 | 同 `apiName` | API 显示名称 |
| `apiPath` | 否 | `{apiName}/openai` | API URL 路径前缀 |
| `backendUrl` | 否 | `https://contoso-ai-eastus2...` | AI 后端服务 URL |
| `_artifactsLocation` | 否 | GitHub raw URL | 预处理 spec 所在目录 |

### 前提条件

1. **APIM 实例已存在**（模板不创建 APIM 本身）
2. **部署者有 Contributor 角色**（直接操作 APIM 子资源）
3. **仓库已 push**（APIM 通过 URL 拉取预处理好的 spec）

### 命令行部署

```powershell
az deployment group create `
  --resource-group apim `
  --template-file azuredeploy-static.json `
  --parameters apiName=ai03
```

---

## 风格 B：云端脚本式

使用 `deploymentScript` 在云端 ACI 容器中自动下载原始 spec、预处理、REST 部署。

> ⚠️ 需要订阅允许存储账户 Key 认证（`AllowSharedKeyAccess`），否则会因 Policy 拒绝而失败。

[![Deploy to Azure (Script)](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fkukisama%2FTrueFluentPro%2Fmain%2FARMs%2Fazuredeploy.json)

### 工作流程

```
点击按钮 → Azure Portal 填参数 → ARM 创建部署
                                      │
                    ┌─────────────────────────────────────┐
                    │  deploymentScript (ACI 容器)          │
                    │                                       │
                    │  1. 下载原始 spec (OpenAPI 3.1)        │
                    │  2. 自动预处理:                        │
                    │     - 3.1 → 3.0.3 降级               │
                    │     - 解析 $ref descriptions          │
                    │     - 去除无 tag 的操作 (49→39)        │
                    │  3. PUT backend (MI 认证)             │
                    │  4. PUT API (inline spec import)      │
                    │  5. PUT policy (set-backend-service)  │
                    │  6. PUT 15 custom operations + tags   │
                    │                                       │
                    │  → 总计 54 operations                 │
                    └─────────────────────────────────────┘
```

### 额外参数

| 参数 | 说明 |
|------|------|
| `specSourceUrl` | 原始 OpenAPI spec URL（可以是 3.1，脚本自动降级） |
| `location` | deploymentScript 容器运行区域 |

### 额外创建的资源

| 资源类型 | 说明 |
|----------|------|
| `UserAssignedIdentity` | `apim-deploy-identity` |
| `RoleAssignment` | Contributor 角色（资源组范围） |
| `deploymentScript` | AzureCLI 容器，运行 `deploy.py` |

---

## 两种风格对比

| | 风格 A（静态） | 风格 B（脚本） |
|--|----------------|----------------|
| **模板** | `azuredeploy-static.json` | `azuredeploy.json` |
| **Spec 来源** | 预处理好的 JSON（仓库中） | 运行时从 Azure REST API Specs 下载 |
| **运行时依赖** | 无 | ACI 容器 + 存储账户 |
| **部署速度** | 快（秒级） | 慢（需等 ACI 启动 + 脚本执行） |
| **存储 Key 要求** | 无 | 需要 `AllowSharedKeyAccess` |
| **额外资源** | 无 | MI + RoleAssignment + deploymentScript |
| **幂等性** | PUT upsert | PUT upsert |
| **适用场景** | 生产环境、严格 Policy 订阅 | 需要自动追踪最新 spec 版本 |

---

## 幂等性

- 所有操作均使用 PUT（upsert）语义
- 重复部署同一 `apiName` → 更新而非报错

## 文件说明

| 文件 | 说明 |
|------|------|
| `azuredeploy-static.json` | **风格 A** — 纯声明式 ARM 模板 |
| `openai-spec-processed.json` | 预处理好的 OpenAPI 3.0.3 spec（39 ops） |
| `preprocess_spec.py` | 生成预处理 spec 的脚本（本地运行一次） |
| `azuredeploy.json` | **风格 B** — deploymentScript ARM 模板 |
| `deploy.py` | 云端部署脚本（由风格 B 的 ACI 容器运行） |
| `README.md` | 本文件 |

> 仓库地址：https://github.com/kukisama/TrueFluentPro/tree/main/ARMs
