import { generateColors } from '@mantine/colors-generator'
import {
  Affix,
  Alert,
  ActionIcon,
  Badge,
  Box,
  Button,
  ColorInput,
  Divider,
  FileInput,
  Grid,
  Group,
  InputBase,
  NumberInput,
  Paper,
  PasswordInput,
  Select,
  SimpleGrid,
  Stack,
  Switch,
  Tabs,
  Text,
  TextInput,
  ThemeIcon,
  Title,
  Tooltip,
  useMantineTheme,
} from '@mantine/core'
import {
  mdiAccountGroupOutline,
  mdiAlert,
  mdiCheck,
  mdiContentSaveOutline,
  mdiCubeOutline,
  mdiDotsHorizontal,
  mdiEmailOutline,
  mdiHammerWrench,
  mdiHeartPulse,
  mdiInformationOutline,
  mdiPackageVariantClosed,
  mdiRestore,
  mdiShieldCheckOutline,
  mdiViewDashboardOutline,
} from '@mdi/js'
import { Icon } from '@mdi/react'
import { FC, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { showNotification } from '@mantine/notifications'
import { ColorPreview } from '@Components/ColorPreview'
import { LogoBox } from '@Components/LogoBox'
import { AdminPage } from '@Components/admin/AdminPage'
import { SwitchLabel } from '@Components/admin/SwitchLabel'
import { webCryptoAvailable } from '@Utils/Crypto'
import { getInputNumber, showErrorMsg } from '@Utils/Shared'
import { IMAGE_MIME_TYPES } from '@Utils/Shared'
import { OnceSWRConfig, useCaptchaConfig, useConfig } from '@Hooks/useConfig'
import api, {
  AccountPolicy,
  BuildRegistryConfig,
  CaptchaConfig,
  CaptchaProvider,
  ConfigEditModel,
  ContainerPolicy,
  EmailConfig,
  GlobalConfig,
  MyIpInfoModel,
  RegistryConfig,
} from '@Api'
import misc from '@Styles/Misc.module.css'

const Configs: FC = () => {
  const { data: configs, mutate } = api.admin.useAdminGetConfigs(OnceSWRConfig)
  const { mutate: mutateCaptchaConfig } = useCaptchaConfig()

  const { mutate: mutateConfig } = useConfig()
  const [disabled, setDisabled] = useState(false)
  const [globalConfig, setGlobalConfig] = useState<GlobalConfig | null>()
  const [accountPolicy, setAccountPolicy] = useState<AccountPolicy | null>()
  const [containerPolicy, setContainerPolicy] = useState<ContainerPolicy | null>()
  const [buildRegistry, setBuildRegistry] = useState<BuildRegistryConfig | null>()
  const [email, setEmail] = useState<EmailConfig | null>()
  const [captcha, setCaptcha] = useState<CaptchaConfig | null>()
  const [registry, setRegistry] = useState<RegistryConfig | null>()
  // Local-only state for the "Send test email" button — never
  // persisted, never round-tripped through the Save flow.
  const [testRecipient, setTestRecipient] = useState('')
  const [testing, setTesting] = useState(false)
  const [testingCaptcha, setTestingCaptcha] = useState(false)
  const [checkingIp, setCheckingIp] = useState(false)
  const [ipInfo, setIpInfo] = useState<MyIpInfoModel | null>(null)

  // Sidebar nav + dirty tracking. The snapshot captured on initial
  // load is the comparison baseline — when any field diverges from
  // that snapshot, the sticky save bar lights up.
  type SectionKey =
    | 'platform'
    | 'account'
    | 'container'
    | 'build_registry'
    | 'email'
    | 'captcha'
    | 'registry_pull'
    | 'diagnostics'
  const [activeSection, setActiveSection] = useState<SectionKey>('platform')
  const initialSnapshotRef = useRef<string | null>(null)
  const [color, setColor] = useState<string | undefined | null>(globalConfig?.customTheme)
  const [logoFile, setLogoFile] = useState<File | null>(null)

  const { t } = useTranslation()

  const [saved, setSaved] = useState(true)
  const theme = useMantineTheme()

  useEffect(() => {
    if (configs) {
      setContainerPolicy(configs.containerPolicy)
      setGlobalConfig(configs.globalConfig)
      setAccountPolicy(configs.accountPolicy)
      setBuildRegistry(configs.buildRegistry)
      setEmail(configs.email)
      setCaptcha(configs.captcha)
      setRegistry(configs.registry)
      setColor(configs.globalConfig?.customTheme)
      // Stash baseline for dirty tracking. Identity (referential
      // equality) isn't enough — the SWR cache may return the same
      // object instance after a no-op revalidation, but we want the
      // dirty flag to reset after a save anyway. Stringify wins.
      initialSnapshotRef.current = JSON.stringify({
        globalConfig: configs.globalConfig,
        accountPolicy: configs.accountPolicy,
        containerPolicy: configs.containerPolicy,
        buildRegistry: configs.buildRegistry,
        email: configs.email,
        captcha: configs.captcha,
        registry: configs.registry,
      })
    }
  }, [configs])

  // Recompute the current snapshot on every render — cheap (<10 small
  // objects) and gets us a fresh dirty flag without per-field plumbing.
  const currentSnapshot = JSON.stringify({
    globalConfig: { ...globalConfig, customTheme: color ?? globalConfig?.customTheme },
    accountPolicy,
    containerPolicy,
    buildRegistry,
    email,
    captcha,
    registry,
  })
  const dirty = initialSnapshotRef.current !== null && currentSnapshot !== initialSnapshotRef.current

  // Per-section status, surfaced as a coloured badge in the sidebar
  // so an operator can see at a glance which surfaces are wired up.
  type SectionStatus = 'configured' | 'inactive' | 'attention'
  const statuses: Record<SectionKey, SectionStatus> = useMemo(() => {
    const captchaConfigured =
      captcha?.provider === 'CloudflareTurnstile'
        ? !!(captcha?.siteKey && captcha?.hasSecretKey)
        : captcha?.provider === 'HashPow'
    return {
      // Platform always has defaults; never "off"
      platform: 'configured',
      // Account is just toggles; surface "attention" when neither
      // anti-cheat rule is on, to nudge operators
      account:
        accountPolicy?.requireUniqueIpPerTeamUser || accountPolicy?.requireUniqueFingerprintPerTeamUser
          ? 'configured'
          : 'attention',
      container: 'configured',
      build_registry: buildRegistry?.isConfigured ? 'configured' : 'inactive',
      email: email?.isConfigured ? 'configured' : 'inactive',
      captcha:
        captcha?.provider === 'None' || !captcha?.provider
          ? 'inactive'
          : captchaConfigured
            ? 'configured'
            : 'attention',
      registry_pull: registry?.isConfigured ? 'configured' : 'inactive',
      diagnostics: 'configured',
    }
  }, [accountPolicy, buildRegistry, email, captcha, registry])

  const navItems: { key: SectionKey; icon: string }[] = [
    { key: 'platform', icon: mdiViewDashboardOutline },
    { key: 'account', icon: mdiAccountGroupOutline },
    { key: 'container', icon: mdiCubeOutline },
    { key: 'email', icon: mdiEmailOutline },
    { key: 'captcha', icon: mdiShieldCheckOutline },
    { key: 'registry_pull', icon: mdiPackageVariantClosed },
    { key: 'build_registry', icon: mdiHammerWrench },
    { key: 'diagnostics', icon: mdiHeartPulse },
  ]

  const StatusBadge: FC<{ status: SectionStatus }> = ({ status }) => {
    const colorMap: Record<SectionStatus, string> = {
      configured: 'teal',
      inactive: 'gray',
      attention: 'orange',
    }
    return (
      <Badge size="xs" variant="light" color={colorMap[status]}>
        {t(`admin.content.settings.status.${status}`)}
      </Badge>
    )
  }

  const SectionHelp: FC<{ description: string }> = ({ description }) => (
    <Tooltip label={description} multiline w={320} withArrow position="right">
      <ThemeIcon variant="subtle" color="gray" size="sm" style={{ cursor: 'help' }}>
        <Icon path={mdiInformationOutline} size={0.7} />
      </ThemeIcon>
    </Tooltip>
  )

  const updateConfig = async (conf: ConfigEditModel) => {
    setDisabled(true)

    try {
      await api.admin.adminUpdateConfigs(conf)

      if (logoFile) {
        await api.admin.adminUpdateLogo({ file: logoFile })
      }

      mutate({ ...conf })
      mutateConfig({ ...conf.globalConfig, ...conf.containerPolicy })
      mutateCaptchaConfig()
    } catch (e) {
      showErrorMsg(e, t)
    } finally {
      setDisabled(false)
    }
  }

  const handleSendTest = async () => {
    if (!email || !testRecipient) return
    setTesting(true)
    try {
      await api.admin.adminTestEmail({ config: email, recipient: testRecipient })
      showNotification({
        color: 'teal',
        title: t('common.label.success'),
        message: t('admin.content.settings.email.test_success', { recipient: testRecipient }),
        icon: <Icon path={mdiCheck} size={1} />,
      })
    } catch (e) {
      showErrorMsg(e, t)
    } finally {
      setTesting(false)
    }
  }

  const handleTestCaptcha = async () => {
    if (!captcha) return
    setTestingCaptcha(true)
    try {
      await api.admin.adminTestCaptcha({ config: captcha })
      showNotification({
        color: 'teal',
        title: t('common.label.success'),
        message: t('admin.content.settings.captcha.test_success'),
        icon: <Icon path={mdiCheck} size={1} />,
      })
    } catch (e) {
      showErrorMsg(e, t)
    } finally {
      setTestingCaptcha(false)
    }
  }

  const handleCheckMyIp = async () => {
    setCheckingIp(true)
    try {
      const { data } = await api.admin.adminMyIp()
      setIpInfo(data)
    } catch (e) {
      showErrorMsg(e, t)
    } finally {
      setCheckingIp(false)
    }
  }

  const onResetLogo = async () => {
    setDisabled(true)
    setLogoFile(null)

    try {
      await api.admin.adminResetLogo()
      mutate({ ...configs, globalConfig: { ...globalConfig, faviconHash: '' } })
      mutateConfig({ ...configs, logoUrl: '' })
    } catch (e) {
      showErrorMsg(e, t)
    } finally {
      setDisabled(false)
    }
  }

  const colors = color && /^#[0-9A-F]{6}$/i.test(color) ? generateColors(color) : theme.colors.brand

  const handleSave = () => {
    updateConfig({
      globalConfig: {
        ...globalConfig,
        customTheme: color && /^#[0-9A-F]{6}$/i.test(color) ? color : '',
      },
      accountPolicy,
      containerPolicy,
      buildRegistry,
      email,
      captcha,
      registry,
    })
    setSaved(false)
    setTimeout(() => {
      setSaved(true)
    }, 500)
  }

  return (
    <AdminPage isLoading={!configs}>
      <Group wrap="nowrap" justify="space-between" align="flex-start" w="100%" pb={100}>
        <Tabs
          orientation="vertical"
          value={activeSection}
          onChange={(value) => value && setActiveSection(value as SectionKey)}
          classNames={{
            root: misc.w10rem,
            list: misc.w10rem,
          }}
        >
          <Tabs.List>
            {navItems.map((item) => (
              <Tabs.Tab
                key={item.key}
                value={item.key}
                leftSection={<Icon path={item.icon} size={1} />}
              >
                <Group justify="space-between" wrap="nowrap" gap={4}>
                  <Text size="sm" truncate>
                    {t(`admin.content.settings.nav.${item.key}`)}
                  </Text>
                  <StatusBadge status={statuses[item.key]} />
                </Group>
              </Tabs.Tab>
            ))}
          </Tabs.List>
        </Tabs>
        <Stack w="calc(100% - 11rem)" gap="md">
        {activeSection === 'platform' && (
        <Stack gap="sm">
          <Group justify="space-between">
            <Title order={2}>{t('admin.content.settings.platform.title')}</Title>
            <SectionHelp description={t('admin.content.settings.platform.api_encryption.description')} />
          </Group>
          <Divider />
          <Grid columns={2} align="center">
            <Grid.Col span={1}>
              <TextInput
                label={t('admin.content.settings.platform.name.label')}
                description={t('admin.content.settings.platform.name.description')}
                placeholder="GZ"
                disabled={disabled}
                value={globalConfig?.title ?? ''}
                onChange={(e) => {
                  setGlobalConfig({ ...globalConfig, title: e.currentTarget.value })
                }}
              />
            </Grid.Col>
            <Grid.Col span={1}>
              <TextInput
                label={t('admin.content.settings.platform.slogan.label')}
                description={t('admin.content.settings.platform.slogan.description')}
                placeholder="Hack for fun not for profit"
                disabled={disabled}
                value={globalConfig?.slogan ?? ''}
                onChange={(e) => {
                  setGlobalConfig({ ...globalConfig, slogan: e.currentTarget.value })
                }}
              />
            </Grid.Col>
            <Grid.Col span={1}>
              <FileInput
                size="sm"
                label={t('admin.content.settings.platform.logo.label')}
                description={t('admin.content.settings.platform.logo.description')}
                placeholder={
                  globalConfig?.faviconHash
                    ? t('admin.placeholder.settings.logo.custom')
                    : t('admin.placeholder.settings.logo.default')
                }
                disabled={disabled}
                accept={IMAGE_MIME_TYPES.join(',')}
                value={logoFile}
                onChange={setLogoFile}
                rightSection={
                  <Tooltip label={t('common.button.reset')}>
                    <ActionIcon onClick={onResetLogo}>
                      <Icon path={mdiRestore} size={0.85} />
                    </ActionIcon>
                  </Tooltip>
                }
              />
            </Grid.Col>
            <Grid.Col p={0} span={1}>
              <Group gap="sm" align="flex-end" justify="center">
                {[20, 40, 60, 80].map((size) => (
                  <Stack align="center" justify="space-between" gap={0} key={size}>
                    <LogoBox size={size} url={logoFile ? URL.createObjectURL(logoFile) : undefined} />
                    <Text fw="bold" ta="center" size="xs">
                      {size}px
                    </Text>
                  </Stack>
                ))}
              </Group>
            </Grid.Col>
            <Grid.Col span={2}>
              <TextInput
                label={t('admin.content.settings.platform.description.label')}
                description={t('admin.content.settings.platform.description.description')}
                placeholder="GZ::CTF is an open source CTF platform"
                disabled={disabled}
                value={globalConfig?.description ?? ''}
                onChange={(e) => {
                  setGlobalConfig({ ...globalConfig, description: e.currentTarget.value })
                }}
              />
            </Grid.Col>
            <Grid.Col span={1}>
              <ColorInput
                size="sm"
                label={t('admin.content.settings.platform.color.label')}
                description={t('admin.content.settings.platform.color.description')}
                placeholder={t('common.content.color.custom.placeholder')}
                disabled={disabled}
                value={color ?? ''}
                onChange={setColor}
              />
            </Grid.Col>
            <Grid.Col span={1}>
              <InputBase
                label={t('admin.content.settings.platform.color_palette.label')}
                description={t('admin.content.settings.platform.color_palette.description')}
                h="100%"
                variant="unstyled"
                component={ColorPreview}
                colors={colors}
                displayColorsInfo={false}
                classNames={{
                  input: misc.flex,
                }}
              />
            </Grid.Col>
            <Grid.Col span={2}>
              <TextInput
                label={t('admin.content.settings.platform.footer.label')}
                description={t('admin.content.settings.platform.footer.description')}
                placeholder={t('admin.placeholder.settings.footer')}
                disabled={disabled}
                value={globalConfig?.footerInfo ?? ''}
                onChange={(e) => {
                  setGlobalConfig({ ...globalConfig, footerInfo: e.currentTarget.value })
                }}
              />
            </Grid.Col>
            <Grid.Col span={1} className={misc.alignCenter}>
              <Switch
                checked={globalConfig?.apiEncryption ?? false}
                disabled={disabled || !webCryptoAvailable}
                readOnly
                label={SwitchLabel(
                  t('admin.content.settings.platform.api_encryption.label'),
                  t('admin.content.settings.platform.api_encryption.description'),
                  webCryptoAvailable ? null : t('admin.content.settings.platform.api_encryption.not_available')
                )}
                onChange={(e) =>
                  setGlobalConfig({
                    ...globalConfig,
                    apiEncryption: e.currentTarget.checked,
                  })
                }
              />
            </Grid.Col>
          </Grid>
        </Stack>
        )}
        {activeSection === 'account' && (
        <Stack gap="sm">
          <Group justify="space-between">
            <Title order={2}>{t('admin.content.settings.account.title')}</Title>
            <SectionHelp description={t('admin.content.settings.account.unique_ip_per_team_user.description')} />
          </Group>
          <Divider />
          <SimpleGrid cols={{ base: 1, sm: 2, xl: 3 }}>
            <Switch
              checked={accountPolicy?.allowRegister ?? true}
              disabled={disabled}
              label={SwitchLabel(
                t('admin.content.settings.account.allow_register.label'),
                t('admin.content.settings.account.allow_register.description')
              )}
              onChange={(e) =>
                setAccountPolicy({
                  ...accountPolicy,
                  allowRegister: e.currentTarget.checked,
                })
              }
            />
            <Switch
              checked={accountPolicy?.emailConfirmationRequired ?? false}
              disabled={disabled}
              label={SwitchLabel(
                t('admin.content.settings.account.email_confirmation_required.label'),
                t('admin.content.settings.account.email_confirmation_required.description')
              )}
              onChange={(e) =>
                setAccountPolicy({
                  ...accountPolicy,
                  emailConfirmationRequired: e.currentTarget.checked,
                })
              }
            />
            <Switch
              checked={accountPolicy?.activeOnRegister ?? true}
              disabled={disabled}
              label={SwitchLabel(
                t('admin.content.settings.account.auto_active.label'),
                t('admin.content.settings.account.auto_active.description')
              )}
              onChange={(e) =>
                setAccountPolicy({
                  ...accountPolicy,
                  activeOnRegister: e.currentTarget.checked,
                })
              }
            />
            <Switch
              checked={accountPolicy?.useCaptcha ?? false}
              disabled={disabled}
              label={SwitchLabel(
                t('admin.content.settings.account.use_captcha.label'),
                t('admin.content.settings.account.use_captcha.description')
              )}
              onChange={(e) =>
                setAccountPolicy({
                  ...accountPolicy,
                  useCaptcha: e.currentTarget.checked,
                })
              }
            />
            <Switch
              checked={accountPolicy?.enableBrowserFingerprint ?? false}
              disabled={disabled}
              label={SwitchLabel(
                t('admin.content.settings.account.browser_fingerprint.label'),
                t('admin.content.settings.account.browser_fingerprint.description')
              )}
              onChange={(e) =>
                setAccountPolicy({
                  ...accountPolicy,
                  enableBrowserFingerprint: e.currentTarget.checked,
                })
              }
            />
            <Switch
              checked={accountPolicy?.requireUniqueIpPerTeamUser ?? false}
              disabled={disabled}
              label={SwitchLabel(
                t('admin.content.settings.account.unique_ip_per_team_user.label'),
                t('admin.content.settings.account.unique_ip_per_team_user.description')
              )}
              onChange={(e) =>
                setAccountPolicy({
                  ...accountPolicy,
                  requireUniqueIpPerTeamUser: e.currentTarget.checked,
                })
              }
            />
            <Switch
              checked={accountPolicy?.requireUniqueFingerprintPerTeamUser ?? false}
              disabled={disabled}
              label={SwitchLabel(
                t('admin.content.settings.account.unique_fingerprint_per_team_user.label'),
                t('admin.content.settings.account.unique_fingerprint_per_team_user.description')
              )}
              onChange={(e) =>
                setAccountPolicy({
                  ...accountPolicy,
                  requireUniqueFingerprintPerTeamUser: e.currentTarget.checked,
                })
              }
            />
          </SimpleGrid>
          {accountPolicy?.enableBrowserFingerprint && (
            <Alert color="yellow" icon={<Icon path={mdiAlert} size={1} />}>
              {t('admin.content.settings.account.browser_fingerprint.warning')}
            </Alert>
          )}
          {(accountPolicy?.requireUniqueIpPerTeamUser || accountPolicy?.requireUniqueFingerprintPerTeamUser) && (
            <Alert color="yellow" icon={<Icon path={mdiAlert} size={1} />}>
              {t('admin.content.settings.account.unique_per_team_user.warning')}
            </Alert>
          )}
          <TextInput
            label={t('admin.content.settings.account.email_domain_list.label')}
            description={t('admin.content.settings.account.email_domain_list.description')}
            placeholder={t('admin.placeholder.settings.email_domain_list')}
            value={accountPolicy?.emailDomainList ?? ''}
            onChange={(e) => {
              setAccountPolicy({ ...accountPolicy, emailDomainList: e.currentTarget.value })
            }}
          />
        </Stack>
        )}
        {activeSection === 'container' && (
        <Stack gap="sm">
          <Group justify="space-between">
            <Title order={2}>{t('admin.content.settings.container.title')}</Title>
            <SectionHelp description={t('admin.content.settings.container.default_lifetime.description')} />
          </Group>
          <Divider />
          <SimpleGrid cols={{ base: 1, sm: 2, xl: 3 }} className={misc.alignCenter}>
            <NumberInput
              label={t('admin.content.settings.container.default_lifetime.label')}
              description={t('admin.content.settings.container.default_lifetime.description')}
              placeholder="120"
              min={1}
              max={7200}
              disabled={disabled}
              value={containerPolicy?.defaultLifetime ?? 120}
              onChange={(e) => {
                const number = getInputNumber(e)
                if (isNaN(number)) return
                setContainerPolicy({ ...containerPolicy, defaultLifetime: number })
              }}
            />
            <NumberInput
              label={t('admin.content.settings.container.extension_duration.label')}
              description={t('admin.content.settings.container.extension_duration.description')}
              placeholder="120"
              min={1}
              max={7200}
              disabled={disabled}
              value={containerPolicy?.extensionDuration ?? 120}
              onChange={(e) => {
                const number = getInputNumber(e)
                if (isNaN(number)) return
                setContainerPolicy({ ...containerPolicy, extensionDuration: number })
              }}
            />
            <NumberInput
              label={t('admin.content.settings.container.renewal_window.label')}
              description={t('admin.content.settings.container.renewal_window.description')}
              placeholder="10"
              min={1}
              max={360}
              disabled={disabled}
              value={containerPolicy?.renewalWindow ?? 10}
              onChange={(e) => {
                const number = getInputNumber(e)
                if (isNaN(number)) return
                setContainerPolicy({ ...containerPolicy, renewalWindow: number })
              }}
            />
            <Switch
              checked={containerPolicy?.autoDestroyOnLimitReached ?? true}
              disabled={disabled}
              label={SwitchLabel(
                t('admin.content.settings.container.auto_destroy.label'),
                t('admin.content.settings.container.auto_destroy.description')
              )}
              onChange={(e) =>
                setContainerPolicy({
                  ...containerPolicy,
                  autoDestroyOnLimitReached: e.currentTarget.checked,
                })
              }
            />
          </SimpleGrid>
        </Stack>
        )}
        {activeSection === 'build_registry' && (
        <Stack gap="sm">
          <Group justify="space-between">
            <Title order={2}>{t('admin.content.settings.build_registry.title')}</Title>
            <SectionHelp description={t('admin.content.settings.build_registry.description')} />
          </Group>
          <Text size="sm" c="dimmed">
            {t('admin.content.settings.build_registry.description')}
          </Text>
          <Divider />
          <Switch
            checked={buildRegistry?.pushOnBuild ?? false}
            disabled={disabled}
            label={SwitchLabel(
              t('admin.content.settings.build_registry.push_on_build.label'),
              t('admin.content.settings.build_registry.push_on_build.description')
            )}
            onChange={(e) =>
              setBuildRegistry({ ...buildRegistry, pushOnBuild: e.currentTarget.checked })
            }
          />
          {buildRegistry?.pushOnBuild && (
            <SimpleGrid cols={2}>
              <TextInput
                label={t('admin.content.settings.build_registry.server.label')}
                description={t('admin.content.settings.build_registry.server.description')}
                placeholder="ghcr.io"
                disabled={disabled}
                value={buildRegistry?.server ?? ''}
                onChange={(e) =>
                  setBuildRegistry({ ...buildRegistry, server: e.currentTarget.value })
                }
              />
              <TextInput
                label={t('admin.content.settings.build_registry.namespace.label')}
                description={t('admin.content.settings.build_registry.namespace.description')}
                placeholder="myorg"
                disabled={disabled}
                value={buildRegistry?.namespace ?? ''}
                onChange={(e) =>
                  setBuildRegistry({ ...buildRegistry, namespace: e.currentTarget.value })
                }
              />
              <TextInput
                label={t('admin.content.settings.build_registry.username.label')}
                description={t('admin.content.settings.build_registry.username.description')}
                disabled={disabled}
                value={buildRegistry?.username ?? ''}
                onChange={(e) =>
                  setBuildRegistry({ ...buildRegistry, username: e.currentTarget.value })
                }
              />
              <PasswordInput
                label={t('admin.content.settings.build_registry.password.label')}
                description={t('admin.content.settings.build_registry.password.description')}
                placeholder={
                  buildRegistry?.hasPassword
                    ? t('admin.content.settings.build_registry.password.configured')
                    : ''
                }
                disabled={disabled}
                value={buildRegistry?.password ?? ''}
                onChange={(e) =>
                  setBuildRegistry({ ...buildRegistry, password: e.currentTarget.value })
                }
              />
            </SimpleGrid>
          )}
        </Stack>

        )}
        {activeSection === 'email' && (
        <Stack gap="sm">
          <Group justify="space-between">
            <Title order={2}>{t('admin.content.settings.email.title')}</Title>
            <SectionHelp description={t('admin.content.settings.email.description')} />
          </Group>
          <Text size="sm" c="dimmed">
            {t('admin.content.settings.email.description')}
          </Text>
          <Divider />
          <SimpleGrid cols={2}>
            <TextInput
              label={t('admin.content.settings.email.smtp_host.label')}
              description={t('admin.content.settings.email.smtp_host.description')}
              placeholder="smtp.example.com"
              disabled={disabled}
              value={email?.smtp?.host ?? ''}
              onChange={(e) =>
                setEmail({ ...email, smtp: { ...email?.smtp, host: e.currentTarget.value } })
              }
            />
            <NumberInput
              label={t('admin.content.settings.email.smtp_port.label')}
              description={t('admin.content.settings.email.smtp_port.description')}
              placeholder="587"
              min={1}
              max={65535}
              disabled={disabled}
              value={email?.smtp?.port ?? 587}
              onChange={(e) =>
                setEmail({ ...email, smtp: { ...email?.smtp, port: getInputNumber(e) || 587 } })
              }
            />
            <TextInput
              label={t('admin.content.settings.email.sender_address.label')}
              description={t('admin.content.settings.email.sender_address.description')}
              placeholder="noreply@example.com"
              disabled={disabled}
              value={email?.senderAddress ?? ''}
              onChange={(e) => setEmail({ ...email, senderAddress: e.currentTarget.value })}
            />
            <TextInput
              label={t('admin.content.settings.email.sender_name.label')}
              description={t('admin.content.settings.email.sender_name.description')}
              placeholder="GZCTF"
              disabled={disabled}
              value={email?.senderName ?? ''}
              onChange={(e) => setEmail({ ...email, senderName: e.currentTarget.value })}
            />
            <TextInput
              label={t('admin.content.settings.email.username.label')}
              description={t('admin.content.settings.email.username.description')}
              disabled={disabled}
              value={email?.userName ?? ''}
              onChange={(e) => setEmail({ ...email, userName: e.currentTarget.value })}
            />
            <PasswordInput
              label={t('admin.content.settings.email.password.label')}
              description={t('admin.content.settings.email.password.description')}
              placeholder={
                email?.hasPassword
                  ? t('admin.content.settings.email.password.configured')
                  : ''
              }
              disabled={disabled}
              value={email?.password ?? ''}
              onChange={(e) => setEmail({ ...email, password: e.currentTarget.value })}
            />
          </SimpleGrid>
          <Switch
            checked={email?.smtp?.bypassCertVerify ?? false}
            disabled={disabled}
            label={SwitchLabel(
              t('admin.content.settings.email.bypass_cert.label'),
              t('admin.content.settings.email.bypass_cert.description')
            )}
            onChange={(e) =>
              setEmail({
                ...email,
                smtp: { ...email?.smtp, bypassCertVerify: e.currentTarget.checked },
              })
            }
          />
          <Group align="flex-end" gap="xs" wrap="nowrap">
            <TextInput
              style={{ flex: 1 }}
              label={t('admin.content.settings.email.test_recipient.label')}
              description={t('admin.content.settings.email.test_recipient.description')}
              placeholder="you@example.com"
              type="email"
              disabled={disabled || testing}
              value={testRecipient}
              onChange={(e) => setTestRecipient(e.currentTarget.value)}
            />
            <Button
              variant="default"
              loading={testing}
              disabled={
                disabled || !testRecipient || !email?.smtp?.host || !email?.senderAddress
              }
              onClick={handleSendTest}
            >
              {t('admin.content.settings.email.test_button')}
            </Button>
          </Group>
        </Stack>

        )}
        {activeSection === 'captcha' && (
        <Stack gap="sm">
          <Group justify="space-between">
            <Title order={2}>{t('admin.content.settings.captcha.title')}</Title>
            <SectionHelp description={t('admin.content.settings.captcha.description')} />
          </Group>
          <Text size="sm" c="dimmed">
            {t('admin.content.settings.captcha.description')}
          </Text>
          <Divider />
          <Select
            label={t('admin.content.settings.captcha.provider.label')}
            description={t('admin.content.settings.captcha.provider.description')}
            disabled={disabled}
            value={captcha?.provider ?? 'None'}
            data={[
              { value: 'None', label: t('admin.content.settings.captcha.provider.none') },
              { value: 'HashPow', label: 'HashPow (in-browser PoW)' },
              { value: 'CloudflareTurnstile', label: 'Cloudflare Turnstile' },
            ]}
            onChange={(v) =>
              setCaptcha({ ...captcha, provider: (v ?? 'None') as CaptchaProvider })
            }
          />
          {captcha?.provider === 'CloudflareTurnstile' && (
            <SimpleGrid cols={2}>
              <TextInput
                label={t('admin.content.settings.captcha.site_key.label')}
                description={t('admin.content.settings.captcha.site_key.description')}
                disabled={disabled}
                value={captcha?.siteKey ?? ''}
                onChange={(e) => setCaptcha({ ...captcha, siteKey: e.currentTarget.value })}
              />
              <PasswordInput
                label={t('admin.content.settings.captcha.secret_key.label')}
                description={t('admin.content.settings.captcha.secret_key.description')}
                placeholder={
                  captcha?.hasSecretKey
                    ? t('admin.content.settings.captcha.secret_key.configured')
                    : ''
                }
                disabled={disabled}
                value={captcha?.secretKey ?? ''}
                onChange={(e) => setCaptcha({ ...captcha, secretKey: e.currentTarget.value })}
              />
            </SimpleGrid>
          )}
          {captcha?.provider === 'HashPow' && (
            <NumberInput
              label={t('admin.content.settings.captcha.difficulty.label')}
              description={t('admin.content.settings.captcha.difficulty.description')}
              min={8}
              max={48}
              disabled={disabled}
              value={captcha?.hashPow?.difficulty ?? 18}
              onChange={(e) =>
                setCaptcha({
                  ...captcha,
                  hashPow: { ...captcha?.hashPow, difficulty: getInputNumber(e) || 18 },
                })
              }
            />
          )}
          {captcha?.provider && captcha.provider !== 'None' && (
            <Group justify="space-between" gap="xs" wrap="nowrap" align="flex-end">
              <Text size="xs" c="dimmed" style={{ flex: 1 }}>
                {t('admin.content.settings.captcha.test_description')}
              </Text>
              <Button
                variant="default"
                loading={testingCaptcha}
                disabled={disabled}
                onClick={handleTestCaptcha}
              >
                {t('admin.content.settings.captcha.test_button')}
              </Button>
            </Group>
          )}
        </Stack>

        )}
        {activeSection === 'registry_pull' && (
        <Stack gap="sm">
          <Group justify="space-between">
            <Title order={2}>{t('admin.content.settings.registry_pull.title')}</Title>
            <SectionHelp description={t('admin.content.settings.registry_pull.description')} />
          </Group>
          <Text size="sm" c="dimmed">
            {t('admin.content.settings.registry_pull.description')}
          </Text>
          <Divider />
          <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }}>
            <TextInput
              label={t('admin.content.settings.registry_pull.server.label')}
              description={t('admin.content.settings.registry_pull.server.description')}
              placeholder="ghcr.io"
              disabled={disabled}
              value={registry?.serverAddress ?? ''}
              onChange={(e) =>
                setRegistry({ ...registry, serverAddress: e.currentTarget.value })
              }
            />
            <TextInput
              label={t('admin.content.settings.registry_pull.username.label')}
              description={t('admin.content.settings.registry_pull.username.description')}
              disabled={disabled}
              value={registry?.userName ?? ''}
              onChange={(e) => setRegistry({ ...registry, userName: e.currentTarget.value })}
            />
            <PasswordInput
              label={t('admin.content.settings.registry_pull.password.label')}
              description={t('admin.content.settings.registry_pull.password.description')}
              placeholder={
                registry?.hasPassword
                  ? t('admin.content.settings.registry_pull.password.configured')
                  : ''
              }
              disabled={disabled}
              value={registry?.password ?? ''}
              onChange={(e) => setRegistry({ ...registry, password: e.currentTarget.value })}
            />
          </SimpleGrid>
        </Stack>

        )}
        {activeSection === 'diagnostics' && (
        <Stack gap="sm">
          <Group justify="space-between">
            <Title order={2}>{t('admin.content.settings.diagnostics.title')}</Title>
            <SectionHelp description={t('admin.content.settings.diagnostics.description')} />
          </Group>
          <Text size="sm" c="dimmed">
            {t('admin.content.settings.diagnostics.description')}
          </Text>
          <Divider />
          <Group justify="flex-start">
            <Button
              variant="default"
              loading={checkingIp}
              disabled={disabled}
              onClick={handleCheckMyIp}
            >
              {t('admin.content.settings.diagnostics.check_button')}
            </Button>
          </Group>
          {ipInfo && (
            <Stack gap={4}>
              <Alert
                color={ipInfo.proxyTrusted ? 'teal' : 'orange'}
                icon={<Icon path={ipInfo.proxyTrusted ? mdiCheck : mdiAlert} size={1} />}
                title={
                  ipInfo.proxyTrusted
                    ? t('admin.content.settings.diagnostics.proxy_trusted_yes')
                    : t('admin.content.settings.diagnostics.proxy_trusted_no')
                }
              >
                <Stack gap={2}>
                  <Text size="sm">
                    <b>{t('admin.content.settings.diagnostics.detected_ip')}:</b>{' '}
                    <code>{ipInfo.detectedIp || '—'}</code>
                  </Text>
                  <Text size="sm">
                    <b>{t('admin.content.settings.diagnostics.raw_connection_ip')}:</b>{' '}
                    <code>{ipInfo.rawConnectionIp || '—'}</code>
                  </Text>
                  <Text size="sm">
                    <b>{t('admin.content.settings.diagnostics.forwarded_for')}:</b>{' '}
                    <code>
                      {ipInfo.forwardedFor || t('admin.content.settings.diagnostics.no_header')}
                    </code>
                  </Text>
                  <Text size="sm">
                    <b>{t('admin.content.settings.diagnostics.trusted_networks')}:</b>{' '}
                    <code>{ipInfo.trustedNetworks.join(', ') || '—'}</code>
                  </Text>
                </Stack>
              </Alert>
            </Stack>
          )}
        </Stack>
        )}
        </Stack>
      </Group>

      {/* Sticky save bar — only fires the save flow; dirty
         tracking lights the indicator when any field diverges
         from the snapshot captured at first load. */}
      <Affix position={{ bottom: 16, left: '50%' }} style={{ transform: 'translateX(-50%)', zIndex: 100 }}>
        <Paper shadow="md" radius="md" p="sm" withBorder>
          <Group gap="md" align="center">
            <Group gap={6}>
              <Box
                w={8}
                h={8}
                style={{
                  borderRadius: '50%',
                  background: dirty ? 'var(--mantine-color-orange-5)' : 'var(--mantine-color-teal-5)',
                }}
              />
              <Text size="sm" c={dirty ? 'orange' : 'dimmed'}>
                {dirty
                  ? t('admin.content.settings.save_bar.unsaved')
                  : t('admin.content.settings.save_bar.saved')}
              </Text>
            </Group>
            <Button
              size="sm"
              variant="filled"
              leftSection={
                <Icon path={!saved ? mdiDotsHorizontal : dirty ? mdiContentSaveOutline : mdiCheck} size={0.9} />
              }
              onClick={handleSave}
              disabled={!saved || disabled || !dirty}
            >
              {t('admin.button.save')}
            </Button>
          </Group>
        </Paper>
      </Affix>
    </AdminPage>
  )
}

export default Configs
