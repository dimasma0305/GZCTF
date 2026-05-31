import {
  Avatar,
  Badge,
  Center,
  Group,
  Loader,
  Modal,
  ModalProps,
  ScrollArea,
  Stack,
  Text,
  Tooltip,
} from '@mantine/core'
import { mdiAccountGroup, mdiCrown } from '@mdi/js'
import { Icon } from '@mdi/react'
import { FC, ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { ScrollingText } from '@Components/ScrollingText'
import { OnceSWRConfig } from '@Hooks/useConfig'
import api from '@Api'
import modalClasses from '@Styles/ScoreboardItemModal.module.css'

/** One headline stat block in the modal's stats row (engine-specific). */
export interface AdTeamDetailStat {
  label: string
  value: ReactNode
  color?: string
}

export interface AdTeamDetailModalProps extends ModalProps {
  /** Team id from the scoreboard row — drives the roster fetch. */
  teamId?: number | null
  teamName?: string | null
  division?: string | null
  /** Engine-specific headline stats (A&D: attack/SLA/defense…, KotH: ticks…). */
  stats?: AdTeamDetailStat[]
}

/**
 * Team-detail modal for the A&D and KotH boards. The jeopardy
 * {@link ScoreboardItemModal} derives its member list from per-user solve
 * attribution, which the A&D/KotH scoreboard payloads don't carry — so this
 * one fetches the team roster from the public GET /api/team/{id} instead and
 * shows the members (with captain marker), plus the engine-specific headline
 * stats passed in by each board. Styled with the same header CSS module as the
 * jeopardy modal for parity.
 */
export const AdTeamDetailModal: FC<AdTeamDetailModalProps> = (props) => {
  const { teamId, teamName, division, stats, ...modalProps } = props
  const { t } = useTranslation()

  // Public endpoint; only fetch once per open with a real team id.
  const { data: team, error } = api.team.useTeamGetBasicInfo(
    teamId ?? 0,
    OnceSWRConfig,
    !!modalProps.opened && !!teamId
  )

  const members = team?.members ?? []
  const loading = !!teamId && !team && !error

  return (
    <Modal
      {...modalProps}
      classNames={{ header: modalClasses.header, title: modalClasses.titleBar }}
      title={
        <Group justify="left" gap="md" wrap="nowrap" className={modalClasses.titleGroup}>
          <Avatar alt="avatar" src={team?.avatar} size={50} radius="md" className={modalClasses.avatar}>
            {(team?.name ?? teamName)?.slice(0, 1) ?? 'T'}
          </Avatar>
          <Stack gap={0} className={modalClasses.infoWrap}>
            <Group gap={4} wrap="nowrap" className={modalClasses.nameRow}>
              <ScrollingText
                text={team?.name ?? teamName ?? 'Team'}
                size="lg"
                fw="bold"
                className={modalClasses.teamName}
                miw="5rem"
              />
              {!!division && (
                <Badge size="sm" variant="outline" className={modalClasses.divisionBadge}>
                  {division}
                </Badge>
              )}
            </Group>
            <ScrollingText
              text={team?.bio || t('team.placeholder.bio')}
              size="sm"
              className={modalClasses.bioText}
            />
          </Stack>
        </Group>
      }
    >
      <Stack align="center" gap="md">
        {/* Headline stats — engine-specific blocks supplied by the board. */}
        {stats && stats.length > 0 && (
          <Group grow ta="center" w="85%" miw="20rem">
            {stats.map((s) => (
              <Stack key={s.label} gap={2}>
                <Text fw="bold" size="sm" ff="monospace" c={s.color}>
                  {s.value}
                </Text>
                <Text size="xs" fw={500}>
                  {s.label}
                </Text>
              </Stack>
            ))}
          </Group>
        )}

        {/* Members roster ─ the part the jeopardy board exposes on click. */}
        <Stack w="85%" miw="20rem" gap="xs">
          <Group gap={6}>
            <Icon path={mdiAccountGroup} size={0.8} color="var(--mantine-color-dimmed)" />
            <Text size="sm" fw={600} c="dimmed">
              {t('team.label.members', 'Members')}
              {members.length > 0 ? ` (${members.length})` : ''}
            </Text>
          </Group>

          {loading ? (
            <Center mih="6rem">
              <Loader size="sm" />
            </Center>
          ) : error ? (
            <Center mih="4rem">
              <Text size="sm" c="dimmed">
                {t('game.content.scoreboard.ad.team_detail_error', 'Could not load team members.')}
              </Text>
            </Center>
          ) : members.length === 0 ? (
            <Center mih="4rem">
              <Text size="sm" c="dimmed">
                {t('game.content.scoreboard.ad.team_detail_empty', 'No members to show.')}
              </Text>
            </Center>
          ) : (
            <ScrollArea scrollbarSize={6} h="14rem" w="100%" scrollbars="y">
              <Stack gap="xs">
                {members.map((m) => (
                  <Group key={m.id ?? m.userName} gap="sm" wrap="nowrap">
                    <Avatar alt="avatar" src={m.avatar} size={34} radius="xl">
                      {m.userName?.slice(0, 1) ?? 'U'}
                    </Avatar>
                    <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
                      <Group gap={4} wrap="nowrap">
                        <Text size="sm" fw={600} truncate>
                          {m.userName}
                        </Text>
                        {m.captain && (
                          <Tooltip label={t('team.content.role.captain', 'Captain')} withinPortal>
                            <span style={{ display: 'inline-flex' }}>
                              <Icon path={mdiCrown} size={0.6} color="var(--mantine-color-yellow-6)" />
                            </span>
                          </Tooltip>
                        )}
                      </Group>
                      {!!m.bio && (
                        <Text size="xs" c="dimmed" truncate>
                          {m.bio}
                        </Text>
                      )}
                    </Stack>
                  </Group>
                ))}
              </Stack>
            </ScrollArea>
          )}
        </Stack>
      </Stack>
    </Modal>
  )
}
