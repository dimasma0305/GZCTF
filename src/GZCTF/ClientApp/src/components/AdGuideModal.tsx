import {
  Accordion,
  Alert,
  Anchor,
  Box,
  Button,
  Code,
  CopyButton,
  Divider,
  Group,
  List,
  Modal,
  ModalProps,
  ScrollArea,
  Stack,
  Text,
  ThemeIcon,
  Title,
} from '@mantine/core'
import { useDisclosure } from '@mantine/hooks'
import { showNotification } from '@mantine/notifications'
import {
  mdiAlertCircleOutline,
  mdiCheck,
  mdiContentCopy,
  mdiCounter,
  mdiCubeOutline,
  mdiDownload,
  mdiKeyChain,
  mdiRestart,
  mdiShieldHalfFull,
  mdiSwordCross,
  mdiToolboxOutline,
  mdiVpn,
} from '@mdi/js'
import { Icon } from '@mdi/react'
import dayjs from 'dayjs'
import { FC, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { showErrorMsg } from '@Utils/Shared'
import { useAdTokenHint } from '@Hooks/useGame'
import api from '@Api'
import misc from '@Styles/Misc.module.css'

interface AdToolkitModalProps extends ModalProps {
  gameId: number
}

/**
 * Player-facing toolkit for Attack &amp; Defense. Single modal that bundles
 * the actionable pieces (team API token, VPN config download) with the
 * reference docs (rules, scoring math, container ops, do/don't). Renamed
 * from "Player Guide" because half the content is interactive rather than
 * read-only.
 */
export const AdGuideModal: FC<AdToolkitModalProps> = ({ gameId, ...modalProps }) => {
  const { t } = useTranslation()
  const { adTokenHint, mutate: mutateHint } = useAdTokenHint(gameId)

  const [rotating, setRotating] = useState(false)
  const [freshToken, setFreshToken] = useState<string | null>(null)
  const [tokenModalOpen, { open: openTokenModal, close: closeTokenModal }] = useDisclosure(false)

  const apiUrl = `${typeof window !== 'undefined' ? window.location.origin : ''}/api/Game/${gameId}/Ad`

  const onRotate = async () => {
    setRotating(true)
    try {
      const { data } = await api.game.gameAdRotateToken(gameId)
      setFreshToken(data.token)
      openTokenModal()
      mutateHint()
    } catch (e) {
      showErrorMsg(e, t)
    } finally {
      setRotating(false)
    }
  }

  const curlExample = [
    `curl -X POST ${apiUrl}/Submit \\`,
    `  -H "Authorization: Bearer ${adTokenHint?.exists ? adTokenHint.hint : '<your-token>'}" \\`,
    `  -H "Content-Type: application/json" \\`,
    `  -d '{"flags":["flag{captured_from_team_b}","flag{another}"]}'`,
  ].join('\n')

  const responseExample = `{
  "acceptedCount": 1,
  "totalPoints": 10.0,
  "results": [
    { "flag": "flag{captured_from_team_b}",
      "status": "accepted", "points": 10.0,
      "flagPlantedAtRound": 7 },
    { "flag": "flag{another}",
      "status": "wrong",
      "message": "flag not recognized" }
  ]
}`

  return (
    <>
      <Modal
        size="48rem"
        centered
        title={
          <Group gap="sm">
            <ThemeIcon variant="light" color="red" size="lg">
              <Icon path={mdiToolboxOutline} size={1} />
            </ThemeIcon>
            <Title order={4}>
              {t('game.content.ad.guide.title', 'Attack & Defense — Toolkit')}
            </Title>
          </Group>
        }
        {...modalProps}
      >
        <ScrollArea h="70vh" scrollbarSize={6}>
          <Stack gap="md" pr="sm">
            <Text size="sm" c="dimmed">
              {t(
                'game.content.ad.guide.intro',
                'Everything you need to play A&D: your team token, your VPN config, the API contract, and the rules. The first two sections are actionable — token rotation and VPN config download.'
              )}
            </Text>

            <Accordion variant="separated" defaultValue={['token', 'vpn']} radius="md" chevronPosition="left" multiple>
              {/* TOKEN */}
              <Accordion.Item value="token">
                <Accordion.Control
                  icon={<Icon path={mdiKeyChain} size={1} color="var(--mantine-color-orange-6)" />}
                >
                  <Text fw={600}>{t('game.content.ad.guide.token.title', 'Team API token')}</Text>
                </Accordion.Control>
                <Accordion.Panel>
                  <Stack gap="sm">
                    <Text size="sm">
                      {t(
                        'game.content.ad.guide.token.intro',
                        'A team-scoped Bearer token. Your exploit scripts pass it as Authorization: Bearer <token> when submitting captured flags. Captains can generate / rotate; non-captains see the hint only.'
                      )}
                    </Text>
                    <Group justify="space-between" wrap="wrap" gap="xs">
                      <Group gap="xs">
                        <Text size="sm" fw={600}>
                          {t('game.content.ad.guide.token.current', 'Current token')}:
                        </Text>
                        {adTokenHint?.exists ? (
                          <Text size="sm" className={misc.ffmono}>
                            {adTokenHint.hint}
                          </Text>
                        ) : (
                          <Text size="sm" c="dimmed">
                            {t('game.content.ad.no_token_yet', 'No token yet')}
                          </Text>
                        )}
                      </Group>
                      {adTokenHint?.canManage && (
                        <Button
                          size="xs"
                          variant="default"
                          leftSection={<Icon path={mdiKeyChain} size={0.7} />}
                          loading={rotating}
                          onClick={onRotate}
                        >
                          {adTokenHint.exists
                            ? t('game.button.ad.rotate_token', 'Rotate token')
                            : t('game.button.ad.generate_token', 'Generate token')}
                        </Button>
                      )}
                    </Group>
                    {!adTokenHint?.canManage && !adTokenHint?.exists && (
                      <Text size="xs" c="dimmed">
                        {t(
                          'game.content.ad.token_captain_only',
                          'Only your team captain can generate the API token. Ask them to open this panel and click Generate token.'
                        )}
                      </Text>
                    )}
                    {adTokenHint?.exists && (
                      <Text size="xs" c="dimmed">
                        {t('game.content.ad.last_used', 'Last used')}:{' '}
                        {adTokenHint.lastUsedAt
                          ? dayjs(adTokenHint.lastUsedAt).fromNow()
                          : t('game.content.ad.never_used', 'never')}
                      </Text>
                    )}
                  </Stack>
                </Accordion.Panel>
              </Accordion.Item>

              {/* VPN */}
              <Accordion.Item value="vpn">
                <Accordion.Control
                  icon={<Icon path={mdiVpn} size={1} color="var(--mantine-color-cyan-6)" />}
                >
                  <Text fw={600}>{t('game.content.ad.guide.vpn.title', 'VPN config')}</Text>
                </Accordion.Control>
                <Accordion.Panel>
                  <Stack gap="sm">
                    <Text size="sm">
                      {t(
                        'game.content.ad.guide.vpn.intro',
                        'Per-user WireGuard config. The first download generates a fresh keypair + assigns you an IP from the game subnet; subsequent downloads return the same file. Drop the .conf into wg-quick (or wireguard-tools / the WireGuard app) to join the A&D network.'
                      )}
                    </Text>
                    <Group gap="sm">
                      <Button
                        leftSection={<Icon path={mdiDownload} size={0.9} />}
                        component="a"
                        href={api.game.gameAdVpnConfigUrl(gameId)}
                        download
                      >
                        {t('game.button.ad.download_vpn', 'Download .conf')}
                      </Button>
                    </Group>
                    <Alert color="blue" icon={<Icon path={mdiAlertCircleOutline} size={0.9} />}>
                      {t(
                        'game.content.ad.guide.vpn.operator_note',
                        'If the downloaded file contains <unconfigured-server:...> placeholders, your operator has not finished provisioning the WireGuard server. The keypair + assigned IP in your .conf are still valid; the operator just needs to set the server endpoint + server pubkey for the connection to come up.'
                      )}
                    </Alert>
                    <Text size="xs" c="dimmed">
                      {t(
                        'game.content.ad.guide.vpn.linux_hint',
                        'Linux: sudo wg-quick up ./ad-game-….conf. macOS / Windows: import via the official WireGuard app.'
                      )}
                    </Text>
                  </Stack>
                </Accordion.Panel>
              </Accordion.Item>

              {/* ROUNDS */}
              <Accordion.Item value="rounds">
                <Accordion.Control
                  icon={<Icon path={mdiSwordCross} size={1} color="var(--mantine-color-red-6)" />}
                >
                  <Text fw={600}>{t('game.content.ad.guide.rounds.title', 'Rounds & flags')}</Text>
                </Accordion.Control>
                <Accordion.Panel>
                  <List size="sm" spacing={4}>
                    <List.Item>
                      {t(
                        'game.content.ad.guide.rounds.tick',
                        'A round (tick) is the scoring unit. Length is per-challenge (the operator sets it — typically 60–180s).'
                      )}
                    </List.Item>
                    <List.Item>
                      {t(
                        'game.content.ad.guide.rounds.plant',
                        'At each tick the platform writes a fresh flag into your container (the "current flag"). You can see it in the challenge modal — protect it.'
                      )}
                    </List.Item>
                    <List.Item>
                      {t(
                        'game.content.ad.guide.rounds.lifetime',
                        'Old flags stay valid for N ticks (default 5). After that they expire and cannot be submitted for points.'
                      )}
                    </List.Item>
                    <List.Item>
                      {t(
                        'game.content.ad.guide.rounds.warmup',
                        'Round 0 is warmup — no scoring yet, just make sure your service is up.'
                      )}
                    </List.Item>
                  </List>
                </Accordion.Panel>
              </Accordion.Item>

              {/* SCORING */}
              <Accordion.Item value="scoring">
                <Accordion.Control
                  icon={<Icon path={mdiCounter} size={1} color="var(--mantine-color-teal-6)" />}
                >
                  <Text fw={600}>{t('game.content.ad.guide.scoring.title', 'How scoring works')}</Text>
                </Accordion.Control>
                <Accordion.Panel>
                  <Stack gap="sm">
                    <Text size="sm">
                      {t(
                        'game.content.ad.guide.scoring.intro',
                        'Three components add up to your team total:'
                      )}
                    </Text>
                    <List size="sm" spacing="xs">
                      <List.Item icon={<Icon path={mdiSwordCross} size={0.8} color="var(--mantine-color-teal-6)" />}>
                        <Text component="span" fw={600} c="teal">
                          {t('game.content.ad.guide.scoring.attack_label', 'Attack')}:
                        </Text>{' '}
                        <Code className={misc.ffmono}>10 / sqrt(N_capturers)</Code>{' '}
                        {t(
                          'game.content.ad.guide.scoring.attack',
                          'per flag you capture. First capturer gets 10 pts, second ~7.07, third ~5.77, and so on — being fast pays.'
                        )}
                      </List.Item>
                      <List.Item icon={<Icon path={mdiShieldHalfFull} size={0.8} color="var(--mantine-color-red-6)" />}>
                        <Text component="span" fw={600} c="red">
                          {t('game.content.ad.guide.scoring.defense_label', 'Defense loss')}:
                        </Text>{' '}
                        <Code className={misc.ffmono}>2.0 × N_times_captured ^ 0.75</Code>.{' '}
                        {t(
                          'game.content.ad.guide.scoring.defense',
                          'Each time another team captures one of your flags increments your captured-count. Sub-linear so early losses hurt more than later ones — patch fast.'
                        )}
                      </List.Item>
                      <List.Item icon={<Icon path={mdiCounter} size={0.8} color="var(--mantine-color-blue-6)" />}>
                        <Text component="span" fw={600} c="blue">
                          {t('game.content.ad.guide.scoring.sla_label', 'SLA')}:
                        </Text>{' '}
                        <Code className={misc.ffmono}>(ok_checks / total_checks) × 10 × current_round</Code>.{' '}
                        {t(
                          'game.content.ad.guide.scoring.sla',
                          'The platform checks your service every tick. Failed checks (Mumble / Offline) shrink the fraction. Keep your service responding.'
                        )}
                      </List.Item>
                    </List>
                    <Divider />
                    <Text size="sm" fw={600}>
                      {t('game.content.ad.guide.scoring.total_formula', 'Total = Attack + SLA − Defense loss')}
                    </Text>
                  </Stack>
                </Accordion.Panel>
              </Accordion.Item>

              {/* SUBMIT */}
              <Accordion.Item value="submit">
                <Accordion.Control
                  icon={<Icon path={mdiContentCopy} size={1} color="var(--mantine-color-grape-6)" />}
                >
                  <Text fw={600}>{t('game.content.ad.guide.submit.title', 'How to submit captured flags')}</Text>
                </Accordion.Control>
                <Accordion.Panel>
                  <Stack gap="sm">
                    <Text size="sm">
                      {t(
                        'game.content.ad.guide.submit.api_only',
                        'Flag submission is API-only — there is no web form for A&D. Your exploit scripts batch the flags they collected and POST them in one request.'
                      )}
                    </Text>
                    <Text size="sm" fw={600}>
                      {t('game.content.ad.guide.submit.request', 'Submit request')}
                    </Text>
                    <Code block className={misc.ffmono} style={{ fontSize: '0.75rem' }}>
                      {curlExample}
                    </Code>
                    <Group justify="space-between">
                      <Text size="sm" c="dimmed">
                        {t(
                          'game.content.ad.guide.submit.batch_note',
                          'Pass 1 to 100 flag strings per request. Results return in input order so you can correlate.'
                        )}
                      </Text>
                      <CopyButton value={curlExample}>
                        {({ copied, copy }) => (
                          <Button
                            size="compact-xs"
                            variant="subtle"
                            leftSection={<Icon path={mdiContentCopy} size={0.7} />}
                            onClick={copy}
                          >
                            {copied
                              ? t('game.tooltip.copy.copied', 'Copied')
                              : t('game.button.ad.copy_curl', 'Copy curl')}
                          </Button>
                        )}
                      </CopyButton>
                    </Group>
                    <Text size="sm" fw={600}>
                      {t('game.content.ad.guide.submit.response', 'Response shape')}
                    </Text>
                    <Code block className={misc.ffmono} style={{ fontSize: '0.75rem' }}>
                      {responseExample}
                    </Code>
                    <Text size="sm" c="dimmed">
                      {t(
                        'game.content.ad.guide.submit.statuses',
                        'Per-flag status is one of: accepted | duplicate | wrong | expired | self_attack | not_started. Only accepted contributes to your score.'
                      )}
                    </Text>
                  </Stack>
                </Accordion.Panel>
              </Accordion.Item>

              {/* CONTAINER */}
              <Accordion.Item value="container">
                <Accordion.Control
                  icon={<Icon path={mdiCubeOutline} size={1} color="var(--mantine-color-violet-6)" />}
                >
                  <Text fw={600}>{t('game.content.ad.guide.container.title', 'Your container')}</Text>
                </Accordion.Control>
                <Accordion.Panel>
                  <List size="sm" spacing={4}>
                    <List.Item>
                      {t(
                        'game.content.ad.guide.container.ip',
                        "The challenge modal shows your container IP:port — that's where checks run and where attackers point their exploits."
                      )}
                    </List.Item>
                    <List.Item>
                      {t(
                        'game.content.ad.guide.container.patch',
                        'You can patch the binary / web service inside the container live — the platform does NOT redeploy unless you reset.'
                      )}
                    </List.Item>
                    <List.Item>
                      {t(
                        'game.content.ad.guide.container.reset',
                        'Click "Reset" to rebuild back to the baseline image. Useful if your patch broke the service, but you lose SLA during the rebuild and there is a cooldown between resets.'
                      )}
                    </List.Item>
                  </List>
                </Accordion.Panel>
              </Accordion.Item>

              {/* DO / DON'T */}
              <Accordion.Item value="do_dont">
                <Accordion.Control
                  icon={<Icon path={mdiRestart} size={1} color="var(--mantine-color-gray-6)" />}
                >
                  <Text fw={600}>{t('game.content.ad.guide.do_dont.title', "Do / Don't")}</Text>
                </Accordion.Control>
                <Accordion.Panel>
                  <Stack gap="xs">
                    <Text size="sm" fw={600} c="teal">
                      {t('game.content.ad.guide.do_dont.do', 'Do')}
                    </Text>
                    <List size="sm" spacing={2}>
                      <List.Item>
                        {t(
                          'game.content.ad.guide.do_dont.do_patch',
                          'Patch your service before the first attack lands.'
                        )}
                      </List.Item>
                      <List.Item>
                        {t(
                          'game.content.ad.guide.do_dont.do_automate',
                          'Automate flag submission — paste into a loop that fetches the current flag from each target and POSTs the batch.'
                        )}
                      </List.Item>
                      <List.Item>
                        {t(
                          'game.content.ad.guide.do_dont.do_monitor',
                          'Watch your check status badge — if it goes Mumble/Offline you are losing SLA every tick.'
                        )}
                      </List.Item>
                    </List>
                    <Text size="sm" fw={600} c="red" mt="xs">
                      {t('game.content.ad.guide.do_dont.dont', "Don't")}
                    </Text>
                    <List size="sm" spacing={2}>
                      <List.Item>
                        {t(
                          'game.content.ad.guide.do_dont.dont_share',
                          'Share your flags or your token with other teams — operators detect it and apply penalties.'
                        )}
                      </List.Item>
                      <List.Item>
                        {t(
                          'game.content.ad.guide.do_dont.dont_dos',
                          "DoS another team's service or the checker infrastructure — instant DQ."
                        )}
                      </List.Item>
                      <List.Item>
                        {t(
                          'game.content.ad.guide.do_dont.dont_break',
                          'Patch in a way that breaks the legit check (returns 500 to the checker) — you lose SLA equivalent to being offline.'
                        )}
                      </List.Item>
                    </List>
                  </Stack>
                </Accordion.Panel>
              </Accordion.Item>
            </Accordion>

            <Text size="xs" c="dimmed" ta="center">
              {t('game.content.ad.guide.footer', 'Endpoint base: ')}
              <Anchor href={apiUrl} target="_blank" rel="noreferrer" className={misc.ffmono}>
                {apiUrl}
              </Anchor>
            </Text>
          </Stack>
        </ScrollArea>
      </Modal>

      {/* Fresh-token reveal modal — shows plaintext exactly once */}
      <Modal
        opened={tokenModalOpen}
        onClose={() => {
          closeTokenModal()
          setFreshToken(null)
        }}
        title={t('game.content.ad.token_modal.title', 'Your new A&D API token')}
        centered
      >
        <Stack gap="sm">
          <Alert color="orange" icon={<Icon path={mdiAlertCircleOutline} size={1} />}>
            {t(
              'game.content.ad.token_modal.warning',
              'Save this token now — it will not be shown again. The previous token (if any) has been invalidated.'
            )}
          </Alert>
          <Box style={{ position: 'relative' }}>
            <Code block className={misc.ffmono}>
              {freshToken}
            </Code>
          </Box>
          <Group justify="flex-end">
            <CopyButton value={freshToken ?? ''}>
              {({ copied, copy }) => (
                <Button
                  variant="default"
                  leftSection={<Icon path={copied ? mdiCheck : mdiContentCopy} size={0.8} />}
                  onClick={copy}
                >
                  {copied
                    ? t('game.tooltip.copy.copied', 'Copied')
                    : t('game.button.ad.copy_token', 'Copy token')}
                </Button>
              )}
            </CopyButton>
            <Button
              onClick={() => {
                closeTokenModal()
                setFreshToken(null)
              }}
            >
              {t('common.modal.confirm', 'Confirm')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  )
}
