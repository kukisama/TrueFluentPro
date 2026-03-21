# 部署 APIM AI API

一键将 Azure OpenAI API 部署到现有的 APIM 服务实例中。  
**无需本地工具**，ARM 模板在云端自动完成 spec 下载、预处理（3.1→3.0）和全部资源部署。

## 一键部署

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2F%3Ckukisama%3E%2F%3CTrueFluentPro%3E%2Fmain%2FaiazManagement%2FARM%2Fazuredeploy.json)

> **使用前**：将上方链接中的 `<OWNER>/<REPO>` 替换为实际的 GitHub 仓库路径，push 后按钮即可用。

## 工作流程

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

## 参数说明

| 参数 | 必填 | 默认值 | 说明 |
|------|------|--------|------|
| `apiName` | **是** | — | API 标识名（如 `ai03`） |
| `serviceName` | 否 | `APIMzpl` | APIM 服务实例名称 |
| `displayName` | 否 | 同 `apiName` | API 显示名称 |
| `apiPath` | 否 | `{apiName}/openai` | API URL 路径前缀 |
| `backendUrl` | 否 | `https://9-mauszqfu-...` | AI 后端服务 URL |
| `specSourceUrl` | 否 | Azure REST API Specs GitHub | 原始 OpenAPI spec URL（**可以是 3.1，脚本自动降级**） |
| `_artifactsLocation` | 否 | GitHub raw URL | `deploy.py` 所在目录的 base URL |
| `location` | 否 | 资源组所在区域 | deploymentScript 容器运行区域 |

## 前提条件

1. **APIM 实例已存在**（模板不创建 APIM 本身）
2. **部署者需要 Owner 或 User Access Administrator 角色**（模板会自动创建托管身份 + 角色分配）
3. **GitHub 仓库已 push**（deploy.py 通过 `supportingScriptUris` 从 `_artifactsLocation` 下载）

## 模板创建的资源

| 资源类型 | 说明 |
|----------|------|
| `UserAssignedIdentity` | `apim-deploy-identity`，共享给所有 API 部署 |
| `RoleAssignment` | Contributor 角色（在资源组范围） |
| `deploymentScript` | AzureCLI 容器，运行 `deploy.py` |

由 `deploy.py` 通过 REST API 创建的 APIM 子资源：
- `backends/{apiName}-ai-endpoint`
- `apis/{apiName}` (39 ops from spec)
- `apis/{apiName}/policies/policy`
- 15 custom operations + 7 service-level tags + 15 tag associations

## 命令行部署（可选）

```powershell
az deployment group create `
  --resource-group apim `
  --template-file azuredeploy.json `
  --parameters apiName=ai03
```

```bash
# 或指定自定义 spec URL
az deployment group create \
  --resource-group apim \
  --template-file azuredeploy.json \
  --parameters apiName=ai03 \
               specSourceUrl="https://raw.githubusercontent.com/Azure/azure-rest-api-specs/main/..."
```

## 幂等性

- 所有 REST 调用均使用 PUT（upsert）语义
- 托管身份和角色分配跨 API 部署共享，不会重复创建
- 重复部署同一 `apiName` → 更新而非报错

## 文件说明

| 文件 | 说明 |
|------|------|
| `azuredeploy.json` | ARM 模板（入口） |
| `deploy.py` | Python 部署脚本（在云端 ACI 容器中运行） |
| `README.md` | 本文件 |
